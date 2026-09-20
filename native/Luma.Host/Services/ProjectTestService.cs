using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Luma.Host.Bridge;

namespace Luma.Host.Services;

public sealed record ProjectTestTask(string Id, string Label, string Command);
public enum ProjectExecutionMode { Test, Development }
public sealed record ProjectTestCommand(string Directory, string Tool, string[] Arguments, string? ManualCommand = null, ProjectExecutionMode Mode = ProjectExecutionMode.Test);
public interface IProjectTestProcess : IDisposable { bool HasExited { get; } }
public interface IProjectTestTerminal { IProjectTestProcess Start(ProjectTestCommand command, CancellationToken cancellation); }
public interface IProjectTestFiles
{
    byte[]? Read(string directory, string name, int maximumBytes);
    string[] ProjectNames(string directory);
}

public sealed class RealProjectTestFiles : IProjectTestFiles
{
    public byte[]? Read(string directory, string name, int maximumBytes)
    {
        var path = Path.Combine(directory, name);
        try
        {
            // The only nested probes are fixed src-tauri metadata. Never follow a linked
            // metadata directory outside the directory capability.
            void CheckParent()
            {
                if (Path.GetDirectoryName(name) is { Length: > 0 } parent &&
                    (File.GetAttributes(Path.Combine(directory, parent)) & FileAttributes.ReparsePoint) != 0)
                    throw new FolderOperationException(ProtocolErrors.AccessDenied, "项目清单目录不能是链接。");
            }
            CheckParent();
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new FolderOperationException(ProtocolErrors.AccessDenied, "项目清单不能是链接或目录。");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            if (stream.Length > maximumBytes) throw new InvalidDataException("项目清单过大，无法安全识别。");
            using var output = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                if (output.Length + read > maximumBytes) throw new InvalidDataException("项目清单过大，无法安全识别。");
                output.Write(buffer, 0, read);
            }
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new FolderOperationException(ProtocolErrors.AccessDenied, "项目清单已变为链接，请重新展开。");
            CheckParent();
            return output.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    public string[] ProjectNames(string directory)
    {
        // Bound enumeration itself, not just the number of matching project files.
        var entries = Directory.EnumerateFileSystemEntries(directory).Take(65).ToArray();
        if (entries.Length > 64) return [];
        return entries.Where(p => Path.GetExtension(p).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal).ToArray();
    }
}

/// <summary>Explicit-click capability execution; no scanning, command execution or installation during detection.</summary>
public sealed class ProjectTestService
{
    private sealed record Detection(string Tool, string[] Arguments, string Display, string Fingerprint, LaunchConfiguration? Launch = null, ProjectExecutionMode Mode = ProjectExecutionMode.Test);
    private sealed record Capability(string Client, string Project, string Item, string Folder, string Directory, Detection Detection, DateTimeOffset Expires);
    private static readonly SemaphoreSlim Workers = new(2, 2);
    private readonly object _gate = new();
    private readonly Dictionary<string, Capability> _tokens = new();
    private readonly Dictionary<string, IProjectTestProcess> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Client, CancellationTokenSource Cancellation)> _starting = new(StringComparer.OrdinalIgnoreCase);
    private readonly FolderService _folders;
    private readonly IProjectTestFiles _files;
    private readonly IProjectTestTerminal _terminal;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _timeout;

    public ProjectTestService(FolderService folders, IProjectTestTerminal? terminal = null, IProjectTestFiles? files = null,
        Func<DateTimeOffset>? clock = null, TimeSpan? timeout = null)
    {
        _folders = folders; _terminal = terminal ?? new WindowsProjectTestTerminal(); _files = files ?? new RealProjectTestFiles();
        _clock = clock ?? (() => DateTimeOffset.UtcNow); _timeout = timeout ?? TimeSpan.FromSeconds(4);
    }
    public Task<ProjectTestTask?> DetectAsync(string client, string project, string item, string folder) => Bounded(cancel =>
    {
        var context = _folders.ResolveProjectTestContext(client, project, item, folder);
        var directory = context.Directory;
        var detection = Detect(context, cancel);
        if (context != _folders.ResolveProjectTestContext(client, project, item, folder))
            throw Invalid("启动配置已变更，请重新展开目录。");
        cancel.ThrowIfCancellationRequested();
        if (detection is null) return null;
        lock (_gate)
        {
            Prune();
            var previous = _tokens.FirstOrDefault(p => p.Value.Client == client && p.Value.Project == project && p.Value.Item == item && p.Value.Folder == folder && p.Value.Detection.Fingerprint == detection.Fingerprint);
            var id = previous.Key ?? Guid.NewGuid().ToString("N");
            if (previous.Key is null)
            {
                while (_tokens.Count >= 128) _tokens.Remove(_tokens.First().Key);
                _tokens[id] = new(client, project, item, folder, directory, detection, _clock().AddMinutes(2));
            }
            else _tokens[id] = previous.Value with { Expires = _clock().AddMinutes(2) };
            return new ProjectTestTask(id, detection.Launch is not null ? "手动启动" : detection.Mode == ProjectExecutionMode.Development ? "打开项目软件" : "测试软件", detection.Display);
        }
    });

    public Task<bool> RunAsync(string client, string project, string item, string taskId) => Bounded(cancel =>
    {
        Capability cap;
        lock (_gate)
        {
            Prune();
            if (!_tokens.TryGetValue(taskId, out cap!) || cap.Client != client || cap.Project != project || cap.Item != item)
                throw Invalid("测试入口已过期或不匹配，请重新展开目录。");
        }
        var context = _folders.ResolveProjectTestContext(client, project, item, cap.Folder);
        var current = Detect(context, cancel);
        if (!string.Equals(context.Directory, cap.Directory, StringComparison.OrdinalIgnoreCase) || current?.Fingerprint != cap.Detection.Fingerprint ||
            context != _folders.ResolveProjectTestContext(client, project, item, cap.Folder))
            throw Invalid("项目清单或启动配置已变更，请重新展开目录后运行。");
        var directory = current!.Launch is { } launch ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(launch.WorkingDirectory)) : context.Directory;
        cancel.ThrowIfCancellationRequested();
        CancellationTokenSource startCancellation;
        lock (_gate)
        {
            // Recheck detach/expiry after IO, before any side effect.
            if (!_tokens.ContainsKey(taskId) || cap.Expires <= _clock()) throw Invalid("测试入口已过期，请重新展开目录。");
            foreach (var key in _active.Where(p => p.Value.HasExited).Select(p => p.Key).ToArray())
            { _active[key].Dispose(); _active.Remove(key); }
            if (_active.ContainsKey(directory) || _starting.ContainsKey(directory))
                throw new FolderOperationException(ProtocolErrors.Busy, "该目录的测试终端已打开或正在打开，请使用已有终端，关闭后可再次运行。");
            if (_active.Count + _starting.Count >= 16) throw new FolderOperationException(ProtocolErrors.Busy, "测试终端较多，请关闭已有终端后重试。");
            cancel.ThrowIfCancellationRequested();
            startCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            _starting[directory] = (client, startCancellation);
        }
        try
        {
            // PATH probing may block on disconnected shares. It never owns the capability/UI lock.
            var process = _terminal.Start(new(directory, current.Tool, current.Arguments, current.Launch?.Command, current.Mode), startCancellation.Token);
            lock (_gate) _active[directory] = process;
            return true;
        }
        finally
        {
            lock (_gate) { _starting.Remove(directory); startCancellation.Dispose(); }
        }
    });

    public void Detach(string client)
    {
        lock (_gate)
        {
            foreach (var id in _tokens.Where(p => p.Value.Client == client).Select(p => p.Key).ToArray()) _tokens.Remove(id);
            foreach (var pending in _starting.Values.Where(p => p.Client == client)) pending.Cancellation.Cancel();
        }
    }
    private void Prune()
    {
        foreach (var id in _tokens.Where(p => p.Value.Expires <= _clock()).Select(p => p.Key).ToArray()) _tokens.Remove(id);
    }

    private Detection? Detect((string Directory, LaunchConfiguration? Launch) context, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (context.Launch is { } launch)
        {
            var fingerprint = "manual:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(launch, ContractsJson.Options))));
            return new("cmd", [], launch.Command, fingerprint, launch);
        }
        var directory = context.Directory;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[]? Read(string name, int limit = 262144)
        {
            cancel.ThrowIfCancellationRequested();
            var bytes = _files.Read(directory, name, limit);
            hash.AppendData(Encoding.UTF8.GetBytes(name + "\0" + (bytes?.Length ?? -1) + "\0"));
            if (bytes is not null) hash.AppendData(bytes);
            return bytes;
        }
        var package = Read("package.json");
        var locks = new[] { "package-lock.json", "npm-shrinkwrap.json", "pnpm-lock.yaml", "yarn.lock" }
            .Where(name => Read(name, 2 * 1024 * 1024) is not null).ToArray();
        var cargo = Read("Cargo.toml");
        Read("Cargo.lock", 2 * 1024 * 1024);
        var go = Read("go.mod");
        Read("go.sum", 2 * 1024 * 1024);
        var candidates = new List<(string Tool, string[] Arguments, string Display, ProjectExecutionMode Mode)>();
        if (package is not null)
        {
            using var json = JsonDocument.Parse(package, new JsonDocumentOptions { MaxDepth = 32 });
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object)
            {
                var managers = locks.Select(name => name.StartsWith("pnpm") ? "pnpm" : name.StartsWith("yarn") ? "yarn" : "npm").Distinct().ToArray();
                var manager = managers.Length == 0 ? "npm" : managers.Length == 1 ? managers[0] : null;
                if (root.TryGetProperty("packageManager", out var declared))
                {
                    if (declared.ValueKind != JsonValueKind.String) manager = null;
                    else
                    {
                        var declaredName = declared.GetString()!.Split('@')[0];
                        if (declaredName is not ("npm" or "pnpm" or "yarn") || (managers.Length != 0 && declaredName != manager)) manager = null;
                        else manager = declaredName;
                    }
                }
                if (manager is not null)
                {
                    static string? Script(JsonElement values, string name) =>
                        values.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(value.GetString()) && value.GetString()!.Length <= 4096 ? value.GetString() : null;
                    var script = new[] { "test", "dev", "start" }.FirstOrDefault(name => Script(scripts, name) is not null);
                    // An explicit Tauri CLI script plus local dependency and development config
                    // identifies the desktop app. A generic dev script would only open its web server.
                    var tauri = false;
                    if (script != "test" && Script(scripts, "tauri")?.Trim() == "tauri" &&
                        new[] { "dependencies", "devDependencies" }.Any(name => root.TryGetProperty(name, out var dependencies) &&
                            dependencies.ValueKind == JsonValueKind.Object && Script(dependencies, "@tauri-apps/cli") is not null))
                    {
                        var configuration = Read("src-tauri/tauri.conf.json");
                        Read("src-tauri/tauri.windows.conf.json");
                        Read("src-tauri/Cargo.toml");
                        Read("src-tauri/Cargo.lock", 2 * 1024 * 1024);
                        if (configuration is not null)
                        {
                            using var config = JsonDocument.Parse(configuration, new JsonDocumentOptions { MaxDepth = 32 });
                            tauri = config.RootElement.ValueKind == JsonValueKind.Object && config.RootElement.TryGetProperty("build", out var build) &&
                                build.ValueKind == JsonValueKind.Object && (Script(build, "devUrl") is not null || Script(build, "beforeDevCommand") is not null);
                        }
                    }
                    if (tauri) candidates.Add((manager, ["run", "tauri", "dev"], $"{manager} run tauri dev", ProjectExecutionMode.Development));
                    else if (script is not null) candidates.Add((manager, ["run", script], $"{manager} run {script}", script == "test" ? ProjectExecutionMode.Test : ProjectExecutionMode.Development));
                }
            }
        }
        if (cargo is not null && Regex.IsMatch(Encoding.UTF8.GetString(cargo), @"(?m)^\s*\[(package|workspace)\]\s*(#.*)?$"))
            candidates.Add(("cargo", ["test", "--offline"], "cargo test --offline", ProjectExecutionMode.Test));
        if (go is not null && Regex.IsMatch(Encoding.UTF8.GetString(go), @"(?m)^\s*module\s+\S+"))
            candidates.Add(("go", ["test", "./..."], "go test ./...", ProjectExecutionMode.Test));
        var projectNames = _files.ProjectNames(directory);
        // Only one explicitly identified test project is supported, never evaluate MSBuild while detecting.
        foreach (var name in projectNames)
        {
            var project = Read(name);
            if (project is null) continue;
            using var reader = XmlReader.Create(new MemoryStream(project), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 262144 });
            var doc = XDocument.Load(reader);
            var explicitTest = doc.Descendants().Any(e => e.Name.LocalName == "IsTestProject" && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) &&
                !e.AncestorsAndSelf().Any(a => a.Attribute("Condition") is not null));
            if (explicitTest) candidates.Add(("dotnet", ["test", ".\\" + name, "--no-restore"], $"dotnet test \"{name}\" --no-restore", ProjectExecutionMode.Test));
        }
        if (candidates.Count != 1) return null;
        var selected = candidates[0];
        return new(selected.Tool, selected.Arguments, selected.Display, Convert.ToHexString(hash.GetHashAndReset()), Mode: selected.Mode);
    }

    private async Task<T> Bounded<T>(Func<CancellationToken, T> action)
    {
        if (!await Workers.WaitAsync(0)) throw new FolderOperationException(ProtocolErrors.Busy, "项目正在读取，请稍后重试。");
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var work = Task.Run(() => { try { return action(token); } finally { Workers.Release(); } });
        try { return await work.WaitAsync(_timeout); }
        catch (TimeoutException)
        {
            cancellation.Cancel();
            _ = work.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            throw new FolderOperationException(ProtocolErrors.Busy, "项目读取超时，请检查磁盘或网络后重试。");
        }
        catch (Exception ex) when (ex is JsonException or XmlException or InvalidDataException)
        { throw Invalid("项目清单格式无效或过大，无法识别测试命令。"); }
        catch (UnauthorizedAccessException) { throw new FolderOperationException(ProtocolErrors.AccessDenied, "没有权限读取项目或打开测试终端。"); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { throw new FolderOperationException(ProtocolErrors.PathNotFound, "项目目录已移动或删除，请重新展开。"); }
        catch (IOException) { throw new FolderOperationException(ProtocolErrors.IoError, "项目暂时无法读取，请稍后重试。"); }
        catch (OperationCanceledException) { throw new FolderOperationException(ProtocolErrors.Cancelled, "测试终端启动已取消，请重新展开目录。"); }
    }
    private static FolderOperationException Invalid(string message) => new(ProtocolErrors.InvalidRequest, message);
}

public sealed class WindowsProjectTestTerminal : IProjectTestTerminal
{
    public IProjectTestProcess Start(ProjectTestCommand command, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var manualInfo = command.ManualCommand is null ? null : BuildStartInfo(command, "");
        if (!Directory.Exists(command.Directory)) throw new DirectoryNotFoundException("启动工作目录已移动或删除。");
        var tool = command.ManualCommand is null
            ? ResolveTool(command.Tool, command.Directory) ?? throw new FolderOperationException(ProtocolErrors.PathNotFound, $"未找到 {command.Tool}，请安装该项目工具并重新打开 Luma。")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        cancellation.ThrowIfCancellationRequested();
        try
        {
            return new TerminalProcess(Process.Start(manualInfo ?? BuildStartInfo(command, tool)) ?? throw new IOException("终端未启动。"));
        }
        catch (System.ComponentModel.Win32Exception)
        { throw new FolderOperationException(ProtocolErrors.AccessDenied, "无法打开终端，请检查命令工具和工作目录权限。"); }
    }
    public static ProcessStartInfo BuildStartInfo(ProjectTestCommand command, string toolPath)
    {
        if (command.ManualCommand is { } manual)
        {
            if (command.Directory.StartsWith(@"\\", StringComparison.Ordinal) || command.Directory.StartsWith("//", StringComparison.Ordinal))
                throw new FolderOperationException(ProtocolErrors.InvalidRequest, "CMD 工作目录不支持 UNC 或设备路径，请使用本地目录或已映射的网络盘符。");
            // Only the saved, explicitly user-authored CMD body reaches this branch. Cwd remains a separate process field.
            return new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                Arguments = "/d /s /k \"" + manual + "\"",
                WorkingDirectory = command.Directory, UseShellExecute = false, CreateNoWindow = false,
            };
        }
        // The command body contains only host-selected executables and literal arguments, never manifest script bodies.
        static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
        var args = string.Join(",", command.Arguments.Select(Literal));
        // Offline testing is a separate host policy from an explicitly clicked development
        // launch. Development inherits the user's environment, just like a saved manual
        // command: never force network on, clear user restrictions, or inject install/fetch.
        var environment = command.Mode == ProjectExecutionMode.Development
            ? "Write-Host '项目开发启动：沿用当前环境；构建脚本可能按项目配置下载依赖。'; "
            : "$env:COREPACK_ENABLE_NETWORK='0'; $env:COREPACK_ENABLE_AUTO_PIN='0'; $env:CARGO_NET_OFFLINE='true'; $env:GOPROXY='off'; $env:GONOPROXY='none'; $env:GOSUMDB='off'; $env:GOVCS='*:off'; $env:GOTOOLCHAIN='local'; ";
        var script = "$ErrorActionPreference='Stop'; " +
            environment +
            "Set-Location -LiteralPath " + Literal(command.Directory) + "; " +
            "$testArgs=@(" + args + "); & " + Literal(toolPath) + " @testArgs; " +
            "Write-Host ('项目进程退出码: ' + $LASTEXITCODE)";
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            WorkingDirectory = command.Directory, UseShellExecute = false, CreateNoWindow = false,
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NoExit", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) info.ArgumentList.Add(arg);
        return info;
    }
    private static string? ResolveTool(string tool, string project)
    {
        if (tool is not ("npm" or "pnpm" or "yarn" or "cargo" or "go" or "dotnet")) return null;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Take(128))
        {
            var path = directory.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(path)) continue;
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (full.Equals(project, StringComparison.OrdinalIgnoreCase) || full.StartsWith(project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var extension in tool is "npm" or "pnpm" or "yarn" ? new[] { ".exe", ".cmd" } : new[] { ".exe" })
            {
                var candidate = Path.Combine(full, tool + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
    private sealed class TerminalProcess(Process process) : IProjectTestProcess
    {
        public bool HasExited => process.HasExited;
        public void Dispose() => process.Dispose();
    }
}
