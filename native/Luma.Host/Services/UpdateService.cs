using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Luma.Host.Services;

/// <summary>update.check 结果；序列化走 ContractsJson camelCase，与前端契约字段一致。</summary>
public sealed record UpdateAssetInfo(string Name, string Url, long Size, string? Sha256Url);
public sealed record UpdateCheckInfo(string CurrentVersion, string LatestVersion, bool HasUpdate, string Notes, string PublishedAt, string InstallMode, UpdateAssetInfo? Asset);
public sealed record UpdateDownloadInfo(string Path, bool Verified, long Bytes);
public sealed record UpdateApplyInfo(bool Accepted, string Mode);

/// <summary>
/// 软件内更新：发布源固定为 GitHub sfex1320/luma-quick-launch 的 Releases。
/// 下载仅允许 https 且域名在白名单内；有 .sha256 时校验一致才算 verified。
/// 便携模式写 apply.ps1 由隐藏 PowerShell 在本实例退出后替换文件并重启；安装模式直接运行安装程序。
/// 退出当前实例的回调由 App 注入，本类不依赖 Application.Current。
/// </summary>
public sealed class UpdateService
{
    public const string PortableMode = "portable";
    public const string InstallerMode = "installer";
    private const string LatestReleaseUrl = "https://api.github.com/repos/sfex1320/luma-quick-launch/releases/latest";
    private const long MaxDownloadBytes = 600L * 1024 * 1024;
    private const int MaxReleaseInfoBytes = 4 * 1024 * 1024;
    private const int MaxHashFileBytes = 64 * 1024;
    private static readonly string[] AllowedHosts = ["api.github.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    /// <summary>更新包文件名白名单：防路径穿越，同时限定只能下载发布资产形态。</summary>
    internal static readonly Regex PackageFileNamePattern = new(@"^luma-quick-launch-[\d.]+-(?:win-x64\.zip|setup-x64\.exe)$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex PackageVersionPattern = new(@"^luma-quick-launch-(?<version>[\d.]+)-", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private readonly Func<HttpMessageHandler> _handler;
    private readonly Action _exitForUpdate;
    private readonly string _executablePath;
    private readonly string _installedPath;
    private readonly string _updatesRoot;
    private readonly string _currentVersion;
    private readonly TimeSpan _timeout;
    private readonly Func<ProcessStartInfo, bool> _processStarter;

    public UpdateService(Action? exitForUpdate = null)
        : this(CreateHandler, exitForUpdate ?? (() => { })) { }

    internal UpdateService(Func<HttpMessageHandler> handler, Action exitForUpdate,
        string? executablePath = null, string? installedPath = null, string? updatesRoot = null,
        string? currentVersion = null, TimeSpan? timeout = null, Func<ProcessStartInfo, bool>? processStarter = null)
    {
        _handler = handler;
        _exitForUpdate = exitForUpdate;
        _executablePath = Path.GetFullPath(executablePath ?? Environment.ProcessPath ?? "Luma.exe");
        _installedPath = Path.GetFullPath(installedPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Luma Quick Launch", "Luma.exe"));
        _updatesRoot = updatesRoot ?? Path.Combine(Path.GetTempPath(), "Luma", "updates");
        _currentVersion = currentVersion ?? HostVersion();
        _timeout = timeout ?? TimeSpan.FromMinutes(15);
        _processStarter = processStarter ?? DefaultStart;
    }

    /// <summary>安装版固定路径命中即 installer，其余（便携/开发）一律 portable。</summary>
    public string InstallMode => string.Equals(_executablePath, _installedPath, StringComparison.OrdinalIgnoreCase)
        ? InstallerMode : PortableMode;

    /// <summary>便携更新脚本启动后由桥接在应答发出之后调用，触发 App 注入的优雅退出。</summary>
    public void SignalExit() => _exitForUpdate();

    /// <summary>查询最新发布：404（含私有仓库不可见）返回固定中文提示；版本比较为三段式数字比较。</summary>
    public async Task<UpdateCheckInfo> CheckAsync()
    {
        using var cancel = new CancellationTokenSource(_timeout);
        using var http = NewClient();
        using var response = await SendWithRedirects(http, new Uri(LatestReleaseUrl), cancel.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new HttpRequestException("无法获取发布信息，请确认仓库已公开或网络可用。");
        EnsureSuccess(response);
        var json = await ReadBounded(response.Content, MaxReleaseInfoBytes, cancel.Token);

        string? tag = null;
        var notes = "";
        var publishedAt = "";
        var assets = new List<(string Name, string Url, long Size)>();
        using (var doc = JsonDocument.Parse(json))
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("tag_name", out var tagEl) && tagEl.ValueKind == JsonValueKind.String) tag = tagEl.GetString();
            if (root.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String) notes = bodyEl.GetString() ?? "";
            if (root.TryGetProperty("published_at", out var publishedEl) && publishedEl.ValueKind == JsonValueKind.String) publishedAt = publishedEl.GetString() ?? "";
            if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var assetEl in assetsEl.EnumerateArray())
                {
                    if (!assetEl.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) continue;
                    if (!assetEl.TryGetProperty("browser_download_url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String) continue;
                    var size = assetEl.TryGetProperty("size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number && sizeEl.TryGetInt64(out var value) ? value : 0;
                    assets.Add((nameEl.GetString()!, urlEl.GetString()!, size));
                }
            }
        }
        if (!TryParseVersion(tag, out var latest))
            throw new HttpRequestException("发布信息缺少有效版本号。");
        // 当前版本无法解析时按 0.0.0 处理，任何正式 release 都视为可更新。
        var hasUpdate = !TryParseVersion(_currentVersion, out var current) || CompareVersions(latest, current) > 0;

        var mode = InstallMode;
        var suffix = mode == InstallerMode ? "-setup-x64.exe" : "-win-x64.zip";
        var selected = assets.FirstOrDefault(a => PackageFileNamePattern.IsMatch(a.Name) && a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        UpdateAssetInfo? asset = null;
        if (selected.Name is { } selectedName)
        {
            var hashUrl = assets.Where(a => string.Equals(a.Name, selectedName + ".sha256", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Url).FirstOrDefault();
            asset = new UpdateAssetInfo(selectedName, selected.Url, selected.Size, hashUrl);
        }
        return new UpdateCheckInfo(_currentVersion, $"{latest.Major}.{latest.Minor}.{latest.Patch}", hasUpdate, notes, publishedAt, mode, asset);
    }

    /// <summary>下载更新包到 %TEMP%\Luma\updates\&lt;版本&gt;\ 下；sha256 不一致即删除文件并报错。</summary>
    public async Task<UpdateDownloadInfo> DownloadAsync(string url, string fileName, string? sha256Url)
    {
        if (!IsAllowedDownloadUrl(url)) throw new ArgumentException("下载地址不被支持。");
        if (!PackageFileNamePattern.IsMatch(fileName)) throw new ArgumentException("更新包文件名不合法。");
        if (sha256Url is not null && !IsAllowedDownloadUrl(sha256Url)) throw new ArgumentException("校验文件地址不被支持。");

        var versionMatch = PackageVersionPattern.Match(fileName);
        var folder = versionMatch.Success ? versionMatch.Groups["version"].Value.TrimEnd('.') : DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var directory = Path.Combine(_updatesRoot, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);

        using var cancel = new CancellationTokenSource(_timeout);
        using var http = NewClient();
        long bytes;
        var verified = false;
        try
        {
            bytes = await DownloadToFile(http, new Uri(url), path, cancel.Token);
            if (sha256Url is not null)
            {
                using var hashResponse = await SendWithRedirects(http, new Uri(sha256Url), cancel.Token);
                EnsureSuccess(hashResponse);
                var expected = ParseSha256Hash(await ReadBounded(hashResponse.Content, MaxHashFileBytes, cancel.Token))
                    ?? throw new HttpRequestException("校验文件格式无效。");
                await using var stream = File.OpenRead(path);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancel.Token));
                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                    throw new HttpRequestException("更新包校验失败，已删除下载文件。");
                verified = true;
            }
        }
        catch
        {
            TryDelete(path);
            throw;
        }
        Log.Info($"update.download 完成：{path}（{bytes} 字节，verified={verified}）");
        return new UpdateDownloadInfo(path, verified, bytes);
    }

    /// <summary>应用更新：path 必须真实存在于下载目录内。便携写脚本+启动隐藏 PowerShell；安装版直接运行安装程序。</summary>
    public Task<UpdateApplyInfo> ApplyAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("更新包路径不能为空。");
        var root = Path.GetFullPath(_updatesRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("更新包必须位于更新下载目录内。");
        if (!File.Exists(full))
            throw new FileNotFoundException("更新包不存在，请重新下载。", full);

        if (InstallMode == InstallerMode)
        {
            if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("安装版更新包必须是安装程序。");
            // 安装器自带 CloseApplications 处理运行中的实例，本进程不主动退出。
            if (!_processStarter(new ProcessStartInfo(full) { UseShellExecute = true }))
                throw new InvalidOperationException("无法启动安装程序。");
            Log.Info($"update.apply 安装版：已启动 {full}");
            return Task.FromResult(new UpdateApplyInfo(true, InstallerMode));
        }

        if (!full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("便携版更新包必须是 ZIP。");
        var script = Path.Combine(Path.GetDirectoryName(full)!, "apply.ps1");
        // 路径可能含中文，必须带 BOM 写入，PowerShell 5.1 才能正确按 UTF-8 解析。
        File.WriteAllText(script, BuildApplyScript(Environment.ProcessId, full,
            Path.GetDirectoryName(_executablePath)!, _executablePath), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var start = new ProcessStartInfo("powershell.exe")
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        if (!_processStarter(start))
            throw new InvalidOperationException("无法启动更新脚本。");
        Log.Info($"update.apply 便携版：替换脚本已启动 {script}");
        return Task.FromResult(new UpdateApplyInfo(true, PortableMode));
    }

    /// <summary>下载地址安全校验：必须 https 且主机在白名单内，不允许内嵌账户信息。</summary>
    internal static bool IsAllowedDownloadUrl(string? value)
    {
        if (value is not { Length: > 0 and <= 4096 }) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        return AllowedHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>三段式语义版本解析：容忍前导 v/V 与短格式，去掉预发布/构建后缀。</summary>
    internal static bool TryParseVersion(string? value, out (int Major, int Minor, int Patch) version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        var cut = text.IndexOfAny(['-', '+']);
        if (cut >= 0) text = text[..cut];
        var parts = text.Split('.');
        if (parts.Length is < 1 or > 3) return false;
        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        version = (numbers[0], numbers[1], numbers[2]);
        return true;
    }

    internal static int CompareVersions((int Major, int Minor, int Patch) a, (int Major, int Minor, int Patch) b)
        => a.Major != b.Major ? a.Major.CompareTo(b.Major)
         : a.Minor != b.Minor ? a.Minor.CompareTo(b.Minor)
         : a.Patch.CompareTo(b.Patch);

    /// <summary>sha256 文件格式为「哈希  文件名」，取首个 64 位十六进制令牌。</summary>
    internal static string? ParseSha256Hash(string text)
    {
        var trimmed = text.TrimStart();
        var end = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
        var token = end < 0 ? trimmed : trimmed[..end];
        return token.Length == 64 && token.All(Uri.IsHexDigit) ? token : null;
    }

    private static string HostVersion()
    {
        var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info)) return "0.0.0";
        var plus = info.IndexOf('+'); // 去掉 SourceRevisionId 后缀
        return plus > 0 ? info[..plus] : info;
    }

    private static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        MaxResponseHeadersLength = 64,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    };

    private HttpClient NewClient()
    {
        var http = new HttpClient(_handler());
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LumaQuickLaunch");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        return http;
    }

    private static bool DefaultStart(ProcessStartInfo start)
    {
        using var process = Process.Start(start);
        return process is not null;
    }

    /// <summary>手动跟随重定向：每一跳都重新校验白名单，禁止跳到任意外部地址。</summary>
    private static async Task<HttpResponseMessage> SendWithRedirects(HttpClient http, Uri source, CancellationToken cancel)
    {
        var current = source;
        for (var redirect = 0; ; redirect++)
        {
            var response = await http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancel);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                response.Dispose();
                if (redirect >= 4) throw new HttpRequestException("更新下载跳转过多。");
                current = new Uri(current, location);
                if (!IsAllowedDownloadUrl(current.AbsoluteUri))
                    throw new HttpRequestException("更新下载跳转到不受信任的地址。");
                continue;
            }
            return response;
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"下载失败（HTTP {(int)response.StatusCode}），请稍后重试。");
    }

    private static async Task<string> ReadBounded(HttpContent content, int maxBytes, CancellationToken cancel)
    {
        if (content.Headers.ContentLength > maxBytes) throw new HttpRequestException("响应内容过大。");
        await using var input = await content.ReadAsStreamAsync(cancel);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, cancel)) > 0)
        {
            if (output.Length + count > maxBytes) throw new HttpRequestException("响应内容过大。");
            output.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static async Task<long> DownloadToFile(HttpClient http, Uri source, string path, CancellationToken cancel)
    {
        using var response = await SendWithRedirects(http, source, cancel);
        EnsureSuccess(response);
        if (response.Content.Headers.ContentLength > MaxDownloadBytes)
            throw new HttpRequestException("更新包超过大小限制（600MB）。");
        await using var input = await response.Content.ReadAsStreamAsync(cancel);
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancel)) > 0)
        {
            total += read;
            if (total > MaxDownloadBytes) throw new HttpRequestException("更新包超过大小限制（600MB）。");
            await output.WriteAsync(buffer.AsMemory(0, read), cancel);
        }
        return total;
    }

    /// <summary>便携替换脚本：等本进程退出 → 探测 Luma.dll 解锁 → 解压 → robocopy /MIR 覆盖 → 以 --startup 重启。</summary>
    private static string BuildApplyScript(int pid, string zipPath, string appDirectory, string executablePath)
    {
        string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var directory = Path.GetDirectoryName(zipPath)!;
        var inner = Path.GetFileNameWithoutExtension(zipPath);
        return string.Join(Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            $"Wait-Process -Id {pid} -Timeout 90 -ErrorAction SilentlyContinue",
            $"$app = {Quote(appDirectory)}",
            $"$exe = {Quote(executablePath)}",
            $"$zip = {Quote(zipPath)}",
            "$dll = Join-Path $app 'Luma.dll'",
            "for ($i = 0; $i -lt 30; $i++) {",
            "    try { $stream = [System.IO.File]::Open($dll, 'Open', 'ReadWrite', 'None'); $stream.Dispose(); break }",
            "    catch { Start-Sleep -Seconds 1 }",
            "}",
            $"$extracted = Join-Path {Quote(directory)} 'extracted'",
            "if (Test-Path $extracted) { Remove-Item $extracted -Recurse -Force }",
            "Expand-Archive -Path $zip -DestinationPath $extracted -Force",
            $"$inner = Join-Path $extracted {Quote(inner)}",
            "if (-not (Test-Path $inner)) { $inner = (Get-ChildItem $extracted -Directory | Select-Object -First 1).FullName }",
            "robocopy $inner $app /MIR /NFL /NDL /NJH /NJS | Out-Null",
            "if ($LASTEXITCODE -ge 8) { exit 1 }",
            "Start-Process -FilePath $exe -ArgumentList '--startup' -WindowStyle Hidden",
            "");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 清理失败不影响错误回报 */ }
    }
}
