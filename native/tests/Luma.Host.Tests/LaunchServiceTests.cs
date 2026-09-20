using System.IO;
using Luma.Host.Services;
using Xunit;
using Path = System.IO.Path;

namespace Luma.Host.Tests;

public class LaunchServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly StateStore _store;
    private readonly FakeShell _shell = new();
    private readonly FakeProbe _probe = new();

    public LaunchServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "luma-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new StateStore(_dir);
        _store.Load();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private LaunchService Service() => new(_store, _shell, _probe, TimeSpan.FromMilliseconds(300));

    [Fact]
    public async Task 同一请求并发到达只启动一次且不阻塞其他ID()
    {
        SaveOneProject("parallel", "D:\\Parallel");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var probes = 0;
        _probe.Hook = _ => { Interlocked.Increment(ref probes); entered.Set(); return release.Wait(3000); };
        var service = new LaunchService(_store, _shell, _probe, TimeSpan.FromSeconds(5));
        var first = Task.Factory.StartNew(() => service.OpenItem("same", "parallel", "main"), TaskCreationOptions.LongRunning);
        Assert.True(entered.Wait(3000));
        var second = Task.Factory.StartNew(() => service.OpenItem("same", "parallel", "main"), TaskCreationOptions.LongRunning);
        var unrelated = Task.Factory.StartNew(() => service.OpenItem("other", "missing", "main"), TaskCreationOptions.LongRunning);
        try
        {
            Assert.Same(unrelated, await Task.WhenAny(unrelated, Task.Delay(2000)));
            await Task.Delay(150);
        }
        finally { release.Set(); }
        var results = await Task.WhenAll(first, second);
        Assert.All(results, r => Assert.True(r.Accepted));
        Assert.Equal(1, probes);
        Assert.Equal(1, _shell.LaunchCount);
    }

    private void SaveOneProject(string projectId, string path)
    {
        var state = TestStates.OneProject(projectId, path);
        state.Revision = _store.Current.Revision;
        var result = _store.Save(state, _store.Current.Revision);
        Assert.Equal(SaveOutcome.Saved, result.Outcome);
    }

    [Fact]
    public void 不存在的项目_返回PATH_NOT_FOUND_不触碰Shell()
    {
        var service = Service();
        var outcome = service.OpenItem("r1", "ghost", "main");
        Assert.False(outcome.Accepted);
        Assert.Equal("PATH_NOT_FOUND", outcome.ErrorCode);
        Assert.Equal(0, _shell.LaunchCount);
    }

    [Fact]
    public void 不存在的入口_返回PATH_NOT_FOUND()
    {
        SaveOneProject("atelier", "D:\\Atelier");
        var outcome = Service().OpenItem("r2", "atelier", "ghost");
        Assert.False(outcome.Accepted);
        Assert.Equal("PATH_NOT_FOUND", outcome.ErrorCode);
    }

    [Fact]
    public void 中文与空格路径_验证通过并启动()
    {
        var path = "D:\\我的项目\\素材 库\\";
        SaveOneProject("atelier", path);
        _probe.Existing.Add(path);
        var outcome = Service().OpenItem("r3", "atelier", "main");
        Assert.True(outcome.Accepted);
        Assert.Equal(path, Assert.Single(_shell.Launched));
    }

    [Theory]
    [InlineData("cmd /c calc")]
    [InlineData("cmd.exe /c del /q D:\\x")]
    [InlineData("powershell -command Get-Process")]
    [InlineData("start notepad")]
    [InlineData("D:\\a && D:\\b")]
    public void 命令串_拒绝且不启动(string path)
    {
        SaveOneProject("atelier", path);
        var outcome = Service().OpenItem("r4-" + path, "atelier", "main");
        Assert.False(outcome.Accepted);
        Assert.Equal("INVALID_REQUEST", outcome.ErrorCode);
        Assert.Equal(0, _shell.LaunchCount);
    }

    [Theory]
    [InlineData("https://example.com/app")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("notepad.exe")]
    [InlineData("relative\\folder")]
    public void URL与相对路径_拒绝(string path)
    {
        SaveOneProject("atelier", path);
        var outcome = Service().OpenItem("r5-" + path, "atelier", "main");
        Assert.False(outcome.Accepted);
        Assert.Equal("INVALID_REQUEST", outcome.ErrorCode);
    }

    [Fact]
    public void 路径含内嵌NUL_拒绝()
    {
        SaveOneProject("atelier", "D:\\bad\0path");
        var outcome = Service().OpenItem("r6", "atelier", "main");
        Assert.False(outcome.Accepted);
        Assert.Equal("INVALID_REQUEST", outcome.ErrorCode);
    }

    [Fact]
    public void 失效路径_探针不可见_返回PATH_NOT_FOUND_不启动()
    {
        SaveOneProject("atelier", "D:\\Moved\\Away");
        var outcome = Service().OpenItem("r7", "atelier", "main");
        Assert.False(outcome.Accepted);
        Assert.Equal("PATH_NOT_FOUND", outcome.ErrorCode);
        Assert.Equal(0, _shell.LaunchCount);
    }

    [Fact]
    public void 同一请求ID_60秒内只启动一次()
    {
        SaveOneProject("atelier", "D:\\Atelier");
        _probe.Existing.Add("D:\\Atelier");
        var service = Service();
        var first = service.OpenItem("same-id", "atelier", "main");
        var second = service.OpenItem("same-id", "atelier", "main");
        Assert.True(first.Accepted);
        Assert.True(second.Accepted);
        Assert.Equal(1, _shell.LaunchCount);
    }

    [Fact]
    public void 不同请求ID_分别启动()
    {
        SaveOneProject("atelier", "D:\\Atelier");
        _probe.Existing.Add("D:\\Atelier");
        var service = Service();
        service.OpenItem("id-a", "atelier", "main");
        service.OpenItem("id-b", "atelier", "main");
        Assert.Equal(2, _shell.LaunchCount);
    }

    [Fact]
    public void 网络路径探针超时_返回PATH_NOT_FOUND_不重试()
    {
        SaveOneProject("atelier", "\\\\offline-nas\\share\\folder");
        var probeCalls = 0;
        _probe.Hook = path =>
        {
            probeCalls++;
            Thread.Sleep(2000); // 模拟不可达 UNC 挂起
            return true;
        };
        var service = new LaunchService(_store, _shell, _probe, TimeSpan.FromMilliseconds(200));
        var outcome = service.OpenItem("r8", "atelier", "main");
        Assert.False(outcome.Accepted);
        Assert.Equal("PATH_NOT_FOUND", outcome.ErrorCode);
        Assert.Equal(1, probeCalls); // 超时不重试
        Assert.Equal(0, _shell.LaunchCount);
    }

    [Fact]
    public void Shell返回权限错误_映射ACCESS_DENIED()
    {
        SaveOneProject("atelier", "D:\\Locked");
        _probe.Existing.Add("D:\\Locked");
        _shell.FailWith = "没有权限访问该路径或目标不可用。";
        var outcome = Service().OpenItem("r9", "atelier", "main");
        Assert.False(outcome.Accepted);
        Assert.Equal("ACCESS_DENIED", outcome.ErrorCode);
    }
}

public class PathRulesTests
{
    [Theory]
    [InlineData("D:\\Projects")]
    [InlineData("D:\\我的项目\\素材 库")]
    [InlineData("C:\\Program Files\\App & Tools\\run.exe")]
    [InlineData("\\\\nas\\share\\Projects")]
    [InlineData("E:\\")]
    public void 合法路径_通过(string path)
    {
        Assert.Null(PathRules.Validate(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("folder\\relative")]
    [InlineData("https://example.com")]
    [InlineData("file:///C:/x")]
    [InlineData("cmd /c calc")]
    [InlineData("powershell -EncodedCommand AAA")]
    [InlineData("\"C:\\quoted start\"")]
    [InlineData("D:\\a > D:\\b.txt")]
    public void 非法路径_拒绝并给出中文提示(string path)
    {
        var error = PathRules.Validate(path);
        Assert.NotNull(error);
        Assert.False(error!.Contains("G:\\") || error.Contains("stack"), "错误信息不应暴露环境细节");
    }

    [Fact]
    public void 内嵌NUL_拒绝()
    {
        Assert.NotNull(PathRules.Validate("D:\\x\0y"));
    }
}
