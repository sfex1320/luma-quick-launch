using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

public sealed class UpdateServiceTests : IDisposable
{
    private static readonly string InstalledPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Luma Quick Launch", "Luma.exe");
    private const string ZipName = "luma-quick-launch-0.4.0-win-x64.zip";
    private const string ExeName = "luma-quick-launch-0.4.0-setup-x64.exe";
    private const string ZipUrl = "https://github.com/sfex1320/luma-quick-launch/releases/download/v0.4.0/luma-quick-launch-0.4.0-win-x64.zip";
    private const string ExeUrl = "https://github.com/sfex1320/luma-quick-launch/releases/download/v0.4.0/luma-quick-launch-0.4.0-setup-x64.exe";

    private readonly string _dir;
    private readonly string _updates;

    public UpdateServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "luma-update-tests", Guid.NewGuid().ToString("N"));
        _updates = Path.Combine(_dir, "updates");
        Directory.CreateDirectory(_updates);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>默认构造便携模式（exe 不在安装路径下）；测试可按需覆盖 exe/退出回调/进程启动器。</summary>
    private UpdateService Service(HttpMessageHandler transport, Action? exit = null, string? exe = null,
        Func<ProcessStartInfo, bool>? starter = null, string current = "0.3.7")
        => new(() => transport, exit ?? (() => { }), exe ?? Path.Combine(_dir, "app", "Luma.exe"),
            InstalledPath, _updates, current, null, starter);

    private static string ReleaseJson(string tag, bool withHashAsset, bool withPackages = true)
    {
        var assets = withPackages
            ? $$"""{"name":"{{ZipName}}","browser_download_url":"{{ZipUrl}}","size":123456,"digest":"sha256:aa"},{"name":"{{ExeName}}","browser_download_url":"{{ExeUrl}}","size":234567,"digest":"sha256:bb"}"""
              + (withHashAsset
                ? $$""",{"name":"{{ZipName}}.sha256","browser_download_url":"{{ZipUrl}}.sha256","size":90},{"name":"{{ExeName}}.sha256","browser_download_url":"{{ExeUrl}}.sha256","size":90}"""
                : "")
            : """{"name":"readme.txt","browser_download_url":"https://github.com/sfex1320/luma-quick-launch/releases/download/v0.4.0/readme.txt","size":1}""";
        return $$"""{"tag_name":"{{tag}}","body":"修复若干问题","published_at":"2026-09-23T12:00:00Z","assets":[{{assets}}]}""";
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task CheckAsync_NewerRelease_ReportsUpdateAndSelectsPortableZip()
    {
        var transport = new Transport(_ => Json(ReleaseJson("v0.4.0", withHashAsset: true)));
        var info = await Service(transport).CheckAsync();
        Assert.Equal("0.3.7", info.CurrentVersion);
        Assert.Equal("0.4.0", info.LatestVersion);
        Assert.True(info.HasUpdate);
        Assert.Equal("修复若干问题", info.Notes);
        Assert.Equal("2026-09-23T12:00:00Z", info.PublishedAt);
        Assert.Equal("portable", info.InstallMode);
        var asset = info.Asset;
        Assert.NotNull(asset);
        Assert.Equal(ZipName, asset.Name);
        Assert.Equal(ZipUrl, asset.Url);
        Assert.Equal(123456, asset.Size);
        Assert.Equal(ZipUrl + ".sha256", asset.Sha256Url);
        Assert.Single(transport.Requests);
        Assert.Equal("api.github.com", transport.Requests[0].Host);
    }

    [Fact]
    public async Task CheckAsync_SameVersion_ReportsNoUpdate()
    {
        var transport = new Transport(_ => Json(ReleaseJson("v0.3.7", withHashAsset: true)));
        var info = await Service(transport).CheckAsync();
        Assert.False(info.HasUpdate);
        Assert.Equal("0.3.7", info.LatestVersion);
    }

    [Fact]
    public async Task CheckAsync_TagWithoutLeadingV_IsTolerated()
    {
        var transport = new Transport(_ => Json(ReleaseJson("0.4.0", withHashAsset: false)));
        var info = await Service(transport).CheckAsync();
        Assert.Equal("0.4.0", info.LatestVersion);
        Assert.True(info.HasUpdate);
        // 没有同名 .sha256 资产时 sha256Url 必须为 null（前端据此拒绝应用）。
        Assert.NotNull(info.Asset);
        Assert.Null(info.Asset!.Sha256Url);
    }

    [Fact]
    public async Task CheckAsync_InstalledLocation_SelectsInstallerExe()
    {
        var transport = new Transport(_ => Json(ReleaseJson("v0.4.0", withHashAsset: true)));
        var info = await Service(transport, exe: InstalledPath).CheckAsync();
        Assert.Equal("installer", info.InstallMode);
        Assert.NotNull(info.Asset);
        Assert.Equal(ExeName, info.Asset!.Name);
        Assert.Equal(ExeUrl + ".sha256", info.Asset.Sha256Url);
    }

    [Fact]
    public async Task CheckAsync_NoMatchingPackage_AssetIsNull()
    {
        var transport = new Transport(_ => Json(ReleaseJson("v0.4.0", withHashAsset: false, withPackages: false)));
        var info = await Service(transport).CheckAsync();
        Assert.True(info.HasUpdate);
        Assert.Null(info.Asset);
    }

    [Fact]
    public async Task CheckAsync_NotFound_ReturnsFriendlyMessage()
    {
        var transport = new Transport(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Service(transport).CheckAsync());
        Assert.Contains("无法获取发布信息", ex.Message);
    }

    [Theory]
    [InlineData("v0.4.0", 0, 4, 0)]
    [InlineData("0.4.0", 0, 4, 0)]
    [InlineData("V1.2", 1, 2, 0)]
    [InlineData("1.2.3-beta.1", 1, 2, 3)]
    [InlineData("0.3.7+abcdef", 0, 3, 7)]
    public void TryParseVersion_ToleratesVPrefixShortAndSuffixForms(string tag, int major, int minor, int patch)
    {
        Assert.True(UpdateService.TryParseVersion(tag, out var version));
        Assert.Equal((major, minor, patch), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v1.2.3.4")]
    public void TryParseVersion_RejectsNonSemver(string tag) => Assert.False(UpdateService.TryParseVersion(tag, out _));

    [Theory]
    [InlineData("http://github.com/sfex1320/luma-quick-launch/releases/download/v0.4.0/x.zip")]
    [InlineData("https://evil.example.com/x.zip")]
    [InlineData("https://github.com.evil.example.com/x.zip")]
    [InlineData("https://user:pw@github.com/x.zip")]
    public void DownloadUrl_RequiresHttpsAndWhitelistedHost(string url) => Assert.False(UpdateService.IsAllowedDownloadUrl(url));

    [Theory]
    [InlineData("https://github.com/sfex1320/luma-quick-launch/releases/download/v0.4.0/x.zip")]
    [InlineData("https://api.github.com/repos/sfex1320/luma-quick-launch/releases/latest")]
    [InlineData("https://objects.githubusercontent.com/asset")]
    [InlineData("https://release-assets.githubusercontent.com/asset")]
    public void DownloadUrl_AcceptsWhitelistedHosts(string url) => Assert.True(UpdateService.IsAllowedDownloadUrl(url));

    [Theory]
    [InlineData("http://github.com/sfex1320/luma-quick-launch/releases/download/v0.4.0/x.zip")]
    [InlineData("https://evil.example.com/x.zip")]
    public async Task DownloadAsync_RejectsUntrustedUrlBeforeAnyNetwork(string url)
    {
        var transport = new Transport(_ => throw new InvalidOperationException("不应发起网络请求"));
        await Assert.ThrowsAsync<ArgumentException>(() => Service(transport).DownloadAsync(url, ZipName, null));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task DownloadAsync_RejectsUntrustedSha256UrlBeforeAnyNetwork()
    {
        var transport = new Transport(_ => throw new InvalidOperationException("不应发起网络请求"));
        await Assert.ThrowsAsync<ArgumentException>(() => Service(transport).DownloadAsync(ZipUrl, ZipName, "https://evil.example.com/x.sha256"));
        Assert.Empty(transport.Requests);
    }

    private Transport PackageTransport(byte[] payload, string hashHex, bool redirect = false) => new(request =>
    {
        var uri = request.RequestUri!.AbsoluteUri;
        if (redirect && uri == ZipUrl)
            return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/pkg.zip") } };
        if (uri.EndsWith(".sha256"))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{hashHex}  {ZipName}\n") };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadAsync_MatchingSha256_Verified(bool redirect)
    {
        var payload = Encoding.UTF8.GetBytes("luma-package");
        var hex = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var transport = PackageTransport(payload, hex, redirect);
        var result = await Service(transport).DownloadAsync(ZipUrl, ZipName, ZipUrl + ".sha256");
        Assert.True(result.Verified);
        Assert.Equal(payload.Length, result.Bytes);
        Assert.Equal(Path.Combine(_updates, "0.4.0", ZipName), result.Path);
        Assert.True(File.Exists(result.Path));
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.Path));
    }

    [Fact]
    public async Task DownloadAsync_MismatchedSha256_DeletesFileAndThrows()
    {
        var payload = Encoding.UTF8.GetBytes("luma-package");
        var transport = PackageTransport(payload, new string('0', 64));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Service(transport).DownloadAsync(ZipUrl, ZipName, ZipUrl + ".sha256"));
        Assert.Contains("校验失败", ex.Message);
        Assert.False(File.Exists(Path.Combine(_updates, "0.4.0", ZipName)));
    }

    [Fact]
    public async Task DownloadAsync_WithoutSha256_Unverified()
    {
        var payload = Encoding.UTF8.GetBytes("luma-package");
        var transport = new Transport(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        var result = await Service(transport).DownloadAsync(ZipUrl, ZipName, null);
        Assert.False(result.Verified);
        Assert.True(File.Exists(result.Path));
    }

    [Fact]
    public async Task DownloadAsync_DeclaredLengthOverLimit_NeverWritesFile()
    {
        var content = new ByteArrayContent(new byte[4]);
        content.Headers.ContentLength = 600L * 1024 * 1024 + 1;
        var transport = new Transport(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        await Assert.ThrowsAsync<HttpRequestException>(() => Service(transport).DownloadAsync(ZipUrl, ZipName, null));
        Assert.False(File.Exists(Path.Combine(_updates, "0.4.0", ZipName)));
    }

    [Fact]
    public async Task DownloadAsync_RedirectToUntrustedHost_Refused()
    {
        var transport = new Transport(_ => new HttpResponseMessage(HttpStatusCode.Found)
        { Headers = { Location = new Uri("https://evil.example.com/pkg.zip") } });
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Service(transport).DownloadAsync(ZipUrl, ZipName, null));
        Assert.Contains("不受信任", ex.Message);
        Assert.False(File.Exists(Path.Combine(_updates, "0.4.0", ZipName)));
    }

    [Fact]
    public async Task ApplyAsync_RejectsPathsOutsideUpdatesRoot()
    {
        var service = Service(new Transport(_ => throw new InvalidOperationException("不应发起网络请求")));
        var outside = Path.Combine(_dir, "elsewhere.zip");
        File.WriteAllText(outside, "x");
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyAsync(outside));
        // 相对路径穿越同样按目录外处理。
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyAsync(Path.Combine(_updates, "0.4.0", "..", "..", "elsewhere.zip")));
        // 目录内但文件不存在。
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.ApplyAsync(Path.Combine(_updates, "0.4.0", ZipName)));
    }

    private string CreatePackage(string name)
    {
        var path = Path.Combine(_updates, "0.4.0", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "package");
        return path;
    }

    [Fact]
    public async Task ApplyAsync_Portable_WritesScriptAndStartsHiddenPowerShell()
    {
        var package = CreatePackage(ZipName);
        var starts = new List<ProcessStartInfo>();
        var service = Service(new Transport(_ => throw new InvalidOperationException("不应发起网络请求")), starter: psi => { starts.Add(psi); return true; });
        var applied = await service.ApplyAsync(package);
        Assert.True(applied.Accepted);
        Assert.Equal("portable", applied.Mode);
        var psi = Assert.Single(starts);
        Assert.Equal("powershell.exe", psi.FileName);
        Assert.True(psi.CreateNoWindow);
        Assert.Equal(new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File" }, psi.ArgumentList.Take(4).ToArray());
        var script = psi.ArgumentList[^1];
        Assert.True(File.Exists(script));
        var content = File.ReadAllText(script);
        Assert.Contains($"Wait-Process -Id {Environment.ProcessId}", content);
        Assert.Contains("Expand-Archive", content);
        Assert.Contains("robocopy $inner $app /MIR", content);
        Assert.Contains(package, content);
        Assert.Contains(Path.Combine(_dir, "app"), content);
        Assert.Contains("--startup", content);
    }

    [Fact]
    public async Task ApplyAsync_Installer_StartsSetupExe()
    {
        var package = CreatePackage(ExeName);
        var starts = new List<ProcessStartInfo>();
        var service = Service(new Transport(_ => throw new InvalidOperationException("不应发起网络请求")),
            exe: InstalledPath, starter: psi => { starts.Add(psi); return true; });
        var applied = await service.ApplyAsync(package);
        Assert.True(applied.Accepted);
        Assert.Equal("installer", applied.Mode);
        var psi = Assert.Single(starts);
        Assert.Equal(package, psi.FileName);
        Assert.True(psi.UseShellExecute);
    }

    private (BridgeRouter Router, FakeClient Client) CreateBridge(Func<FakeClient, UpdateService> updaterFactory)
    {
        var store = new StateStore(Path.Combine(_dir, "state-" + Guid.NewGuid().ToString("N")));
        store.Load();
        var launcher = new LaunchService(store, new FakeShell(), new FakeProbe(), TimeSpan.FromMilliseconds(300));
        var client = new FakeClient("settings");
        var router = new BridgeRouter(store, launcher, new FakePicker(), new FakeWindows(), new ImmediateSync(), updater: updaterFactory(client));
        router.Attach(client);
        return (router, client);
    }

    private static string Request(string id, string method, string paramsJson = "{}") =>
        $$"""{"protocol":1,"type":"request","id":"{{id}}","method":"{{method}}","params":{{paramsJson}}}""";

    [Fact]
    public async Task Bridge_UpdateCheck_ReturnsCamelCasePayload()
    {
        var transport = new Transport(_ => Json(ReleaseJson("v0.4.0", withHashAsset: true)));
        var (router, client) = CreateBridge(_ => Service(transport));
        await router.HandleMessage(client, Request("u-check", "update.check"));
        using var doc = JsonDocument.Parse(client.Sent.Last());
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean(), client.Sent.Last());
        var result = root.GetProperty("result");
        Assert.Equal("0.3.7", result.GetProperty("currentVersion").GetString());
        Assert.Equal("0.4.0", result.GetProperty("latestVersion").GetString());
        Assert.True(result.GetProperty("hasUpdate").GetBoolean());
        Assert.Equal("portable", result.GetProperty("installMode").GetString());
        Assert.Equal(ZipName, result.GetProperty("asset").GetProperty("name").GetString());
        Assert.Equal(ZipUrl + ".sha256", result.GetProperty("asset").GetProperty("sha256Url").GetString());
    }

    [Fact]
    public async Task Bridge_UpdateCheck_404_ReturnsInternalErrorWithFriendlyMessage()
    {
        var transport = new Transport(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var (router, client) = CreateBridge(_ => Service(transport));
        await router.HandleMessage(client, Request("u-404", "update.check"));
        using var doc = JsonDocument.Parse(client.Sent.Last());
        var root = doc.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("INTERNAL_ERROR", root.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("无法获取发布信息", root.GetProperty("error").GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("../evil.zip")]
    [InlineData("luma-quick-launch-0.4.0-win-x64.zip/evil")]
    [InlineData("..\\..\\evil.exe")]
    [InlineData("other.zip")]
    public async Task Bridge_UpdateDownload_RejectsUnsafeFileName(string fileName)
    {
        var transport = new Transport(_ => throw new InvalidOperationException("不应发起网络请求"));
        var (router, client) = CreateBridge(_ => Service(transport));
        await router.HandleMessage(client, Request("u-dl", "update.download",
            $$"""{"url":"{{ZipUrl}}","fileName":"{{fileName.Replace("\\", "\\\\")}}"}"""));
        using var doc = JsonDocument.Parse(client.Sent.Last());
        Assert.Equal("INVALID_REQUEST", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Bridge_UpdateDownload_ReturnsPathVerifiedBytes()
    {
        var payload = Encoding.UTF8.GetBytes("luma-package");
        var hex = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var (router, client) = CreateBridge(_ => Service(PackageTransport(payload, hex)));
        await router.HandleMessage(client, Request("u-dl", "update.download",
            $$"""{"url":"{{ZipUrl}}","fileName":"{{ZipName}}","sha256Url":"{{ZipUrl}}.sha256"}"""));
        using var doc = JsonDocument.Parse(client.Sent.Last());
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("verified").GetBoolean());
        Assert.Equal(payload.Length, result.GetProperty("bytes").GetInt64());
        Assert.True(File.Exists(result.GetProperty("path").GetString()));
    }

    [Fact]
    public async Task Bridge_UpdateApply_Portable_RespondsBeforeExitSignal()
    {
        var package = CreatePackage(ZipName);
        var sentAtExit = new List<int>();
        var (router, client) = CreateBridge(c => Service(
            new Transport(_ => throw new InvalidOperationException("不应发起网络请求")),
            exit: () => sentAtExit.Add(c.Sent.Count),
            starter: _ => true));
        await router.HandleMessage(client, Request("u-apply", "update.apply", $$"""{"path":"{{package.Replace("\\", "\\\\")}}"}"""));
        // 退出回调触发时应答已经发出。
        Assert.Equal(new[] { 1 }, sentAtExit);
        using var doc = JsonDocument.Parse(client.Sent[0]);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("result").GetProperty("accepted").GetBoolean());
        Assert.Equal("portable", doc.RootElement.GetProperty("result").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Bridge_UpdateApply_Installer_DoesNotExitSelf()
    {
        var package = CreatePackage(ExeName);
        var exits = 0;
        var (router, client) = CreateBridge(_ => Service(
            new Transport(_ => throw new InvalidOperationException("不应发起网络请求")),
            exit: () => exits++, exe: InstalledPath, starter: _ => true));
        await router.HandleMessage(client, Request("u-apply", "update.apply", $$"""{"path":"{{package.Replace("\\", "\\\\")}}"}"""));
        using var doc = JsonDocument.Parse(client.Sent.Last());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("installer", doc.RootElement.GetProperty("result").GetProperty("mode").GetString());
        Assert.Equal(0, exits);
    }

    [Fact]
    public async Task Bridge_UpdateApply_RejectsOutsidePath()
    {
        var (router, client) = CreateBridge(_ => Service(new Transport(_ => throw new InvalidOperationException("不应发起网络请求"))));
        var outside = Path.Combine(_dir, "elsewhere.zip");
        File.WriteAllText(outside, "x");
        await router.HandleMessage(client, Request("u-out", "update.apply", $$"""{"path":"{{outside.Replace("\\", "\\\\")}}"}"""));
        using var doc = JsonDocument.Parse(client.Sent.Last());
        Assert.Equal("INVALID_REQUEST", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private sealed class Transport(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }
}
