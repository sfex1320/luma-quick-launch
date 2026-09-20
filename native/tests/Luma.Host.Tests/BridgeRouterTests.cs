using System.IO;
using System.Text.Json;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Xunit;
using Path = System.IO.Path;
using Rect = System.Windows.Rect;

namespace Luma.Host.Tests;

public class BridgeRouterTests : IDisposable
{
    private readonly string _dir;
    private readonly StateStore _store;
    private readonly BridgeRouter _router;
    private readonly FakeClient _dock;
    private readonly FakeClient _settings;
    private readonly FakePicker _picker;
    private readonly FakeWindows _windows;
    private readonly FakeShell _shell;
    private readonly FakeProbe _probe;
    private readonly LaunchService _launcher;

    public BridgeRouterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "luma-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new StateStore(_dir);
        _store.Load();
        _shell = new FakeShell();
        _probe = new FakeProbe();
        _launcher = new LaunchService(_store, _shell, _probe, TimeSpan.FromMilliseconds(300));
        _picker = new FakePicker();
        _windows = new FakeWindows();
        _router = new BridgeRouter(_store, _launcher, _picker, _windows, new ImmediateSync());
        _dock = new FakeClient("dock");
        _settings = new FakeClient("settings");
        _router.Attach(_dock);
        _router.Attach(_settings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string Request(string id, string method, string paramsJson = "{}") =>
        $$"""{"protocol":1,"type":"request","id":"{{id}}","method":"{{method}}","params":{{paramsJson}}}""";

    private async Task<JsonElement> Send(FakeClient from, string raw)
    {
        await _router.HandleMessage(from, raw);
        var last = from.Sent.LastOrDefault() ?? throw new InvalidOperationException("没有收到应答");
        return JsonDocument.Parse(last).RootElement.Clone();
    }

    private static async Task<JsonElement> Send(FakeClient from, BridgeRouter router, string raw)
    {
        await router.HandleMessage(from, raw);
        return JsonDocument.Parse(from.Sent.Last()).RootElement.Clone();
    }

    [Fact]
    public async Task FolderList_ReturnsRealChildrenWithoutLaunchOrSave()
    {
        var root = Path.Combine(_dir, "电梯贴");
        Directory.CreateDirectory(Path.Combine(root, "目录2"));
        Directory.CreateDirectory(Path.Combine(root, "目录10"));
        File.WriteAllText(Path.Combine(root, "方案.pptx"), "fixture");
        Assert.Equal(SaveOutcome.Saved, _store.Save(TestStates.OneProject("folder", root), 0).Outcome);
        var response = await Send(_dock, Request("folder-list", "folder.list", """{"projectId":"folder","itemId":"main"}"""));
        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString());
        var result = response.GetProperty("result");
        Assert.Equal(new[] { "目录2", "目录10", "方案.pptx" }, result.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()));
        Assert.Equal(JsonValueKind.Null, result.GetProperty("parentId").ValueKind);
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.Empty(_shell.Launched);
        Assert.Equal(1, _store.Current.Revision);
    }

    [Fact]
    public async Task protocol版本不为1_返回UNSUPPORTED_PROTOCOL()
    {
        var response = await Send(_dock, "{\"protocol\":2,\"type\":\"request\",\"id\":\"1\",\"method\":\"app.getState\"}");
        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Equal("UNSUPPORTED_PROTOCOL", response.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task 损坏配置通过桥接返回IO_ERROR而非空项目()
    {
        File.WriteAllText(_store.MainPath, "broken");
        _store.Load();
        var response = await Send(_settings, Request("bad-store", "app.getState"));
        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Equal("IO_ERROR", response.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task 未知方法_返回METHOD_NOT_FOUND()
    {
        var response = await Send(_dock, Request("1", "app.unknown"));
        Assert.Equal("METHOD_NOT_FOUND", response.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task 坏JSON_返回INVALID_REQUEST()
    {
        var response = await Send(_dock, "{ not json");
        Assert.Equal("INVALID_REQUEST", response.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task getState_空安装_返回空状态()
    {
        var response = await Send(_dock, Request("g1", "app.getState"));
        Assert.True(response.GetProperty("ok").GetBoolean());
        var result = response.GetProperty("result");
        Assert.Equal(1, result.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(0, result.GetProperty("revision").GetInt64());
        Assert.Empty(result.GetProperty("projects").EnumerateArray());
    }

    [Fact]
    public async Task saveState成功_返回新修订_其他窗口收到事件_来源不回声()
    {
        var state = TestStates.OneProject("atelier", "D:\\Atelier");
        state.Revision = 0;
        var payload = JsonSerializer.Serialize(state, ContractsJson.Options);
        var response = await Send(_settings, Request("s1", "app.saveState",
            $$"""{"state":{{payload}},"expectedRevision":0}"""));
        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal(1, response.GetProperty("result").GetProperty("revision").GetInt64());

        // 其他窗口（浮岛）收到 app.stateChanged，管理窗（来源）只收到 response 不收到事件
        Assert.Contains(_dock.Sent, json => json.Contains("\"app.stateChanged\""));
        Assert.DoesNotContain(_settings.Sent, json => json.Contains("\"app.stateChanged\""));
    }

    [Fact]
    public async Task saveState过期_返回REVISION_CONFLICT()
    {
        var state = TestStates.OneProject("a", "D:\\A");
        state.Revision = 3;
        var payload = JsonSerializer.Serialize(state, ContractsJson.Options);
        var response = await Send(_settings, Request("s2", "app.saveState",
            $$"""{"state":{{payload}},"expectedRevision":3}"""));
        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Equal("REVISION_CONFLICT", response.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task openItem_不存在的项目_返回PATH_NOT_FOUND_不启动()
    {
        var response = await Send(_dock, Request("o1", "shell.openItem",
            """{"projectId":"ghost","itemId":"main"}"""));
        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Equal("PATH_NOT_FOUND", response.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, _shell.LaunchCount);
    }

    [Fact]
    public async Task openItem_不存在的入口_返回PATH_NOT_FOUND()
    {
        var state = TestStates.OneProject("atelier", "D:\\Atelier");
        state.Revision = 0;
        var payload = JsonSerializer.Serialize(state, ContractsJson.Options);
        await Send(_dock, Request("s3", "app.saveState", $$"""{"state":{{payload}},"expectedRevision":0}"""));

        var response = await Send(_dock, Request("o2", "shell.openItem",
            """{"projectId":"atelier","itemId":"ghost"}"""));
        Assert.Equal("PATH_NOT_FOUND", response.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task openItem_重复请求ID_只启动一次()
    {
        var state = TestStates.OneProject("atelier", "D:\\Atelier");
        state.Revision = 0;
        var payload = JsonSerializer.Serialize(state, ContractsJson.Options);
        await Send(_dock, Request("s4", "app.saveState", $$"""{"state":{{payload}},"expectedRevision":0}"""));
        _probe.Existing.Add("D:\\Atelier");

        var raw = Request("dup-1", "shell.openItem", """{"projectId":"atelier","itemId":"main"}""");
        var first = await Send(_dock, raw);
        var second = await Send(_dock, raw);
        Assert.True(first.GetProperty("ok").GetBoolean());
        Assert.Equal(second.GetProperty("id").GetString(), first.GetProperty("id").GetString());
        Assert.Equal(1, _shell.LaunchCount);
    }

    [Fact]
    public async Task 保存配置_不触发任何启动()
    {
        var state = TestStates.OneProject("atelier", "D:\\Atelier");
        state.Revision = 0;
        var payload = JsonSerializer.Serialize(state, ContractsJson.Options);
        await Send(_dock, Request("s5", "app.saveState", $$"""{"state":{{payload}},"expectedRevision":0}"""));
        await Send(_dock, Request("s6", "app.saveState", $$"""{"state":{{payload.Replace("\"revision\":0", "\"revision\":1")}},"expectedRevision":1}"""));
        Assert.Equal(0, _shell.LaunchCount);
    }

    [Fact]
    public async Task pickFolder_用户取消_返回null_不修改状态()
    {
        _picker.Result = null;
        var response = await Send(_settings, Request("p1", "shell.pickFolder"));
        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("result").ValueKind);
        Assert.Equal(0, _store.Current.Revision);
        Assert.Equal(1, _picker.Calls);
    }

    [Fact]
    public async Task pickFolder_选择成功_返回path与name()
    {
        _picker.Result = "D:\\Projects\\Example";
        var response = await Send(_settings, Request("p2", "shell.pickFolder"));
        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal("D:\\Projects\\Example", response.GetProperty("result").GetProperty("path").GetString());
        Assert.Equal("Example", response.GetProperty("result").GetProperty("name").GetString());
    }

    [Fact]
    public async Task pickFolder_UNC路径_name取最后一段()
    {
        var name = BridgeRouter.FolderDisplayName(@"\\nas\share\Projects\我的 项目");
        Assert.Equal("我的 项目", name);
    }

    [Fact]
    public async Task windowSync_InvalidInteractionDoesNotReachWindow()
    {
        var response = await Send(_dock, Request("invalid-interaction", "window.sync",
            """{"expanded":false,"interacting":"true","rects":[]}"""));
        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Equal("INVALID_REQUEST", response.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_windows.Syncs);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("9007199254740992")]
    [InlineData("1e100")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("\"1\"")]
    public async Task windowSync_InvalidVisibilityIdDoesNotReachWindow(string value)
    {
        var response = await Send(_dock, Request("invalid-visibility", "window.sync",
            $$"""{"expanded":false,"visibilityId":{{value}},"rects":[]}"""));
        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Equal("INVALID_REQUEST", response.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_windows.Syncs);
    }

    [Fact]
    public async Task windowSync_合法rects_转发给窗口宿主()
    {
        var response = await Send(_dock, Request("w1", "window.sync",
            """{"expanded":true,"rects":[{"x":20,"y":16,"width":640,"height":88}]}"""));
        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.True(response.GetProperty("result").GetProperty("applied").GetBoolean());
        var sync = Assert.Single(_windows.Syncs);
        Assert.True(sync.Expanded);
        Assert.False(sync.Interacting);
        Assert.Null(sync.VisibilityId);
        var rect = Assert.Single(sync.Rects);
        Assert.Equal(new Rect(20, 16, 640, 88), rect);
    }

    [Fact]
    public void windowVisibility_IncludesGenerationAndKeepsLegacyFieldOptional()
    {
        _router.BroadcastVisibility(true, 42);
        foreach (var client in new[] { _dock, _settings })
        {
            using var document = JsonDocument.Parse(client.Sent.Last());
            Assert.Equal("window.visibility", document.RootElement.GetProperty("event").GetString());
            var data = document.RootElement.GetProperty("data");
            Assert.True(data.GetProperty("visible").GetBoolean());
            Assert.Equal(42, data.GetProperty("visibilityId").GetInt64());
        }
        _router.BroadcastVisibility(false);
        using var legacy = JsonDocument.Parse(_dock.Sent.Last());
        Assert.False(legacy.RootElement.GetProperty("data").TryGetProperty("visibilityId", out _));
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("42", 42L)]
    [InlineData("1.0", 1L)]
    [InlineData("1e2", 100L)]
    [InlineData("9007199254740991", 9007199254740991L)]
    public async Task windowSync_ValidVisibilityIdIsForwarded(string value, long expected)
    {
        var response = await Send(_dock, Request("visibility", "window.sync",
            $$"""{"expanded":false,"visibilityId":{{value}},"rects":[]}"""));
        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal(expected, Assert.Single(_windows.Syncs).VisibilityId);
    }

    [Fact]
    public async Task windowSync_InteractionIsForwardedWithoutInferringFromRectCount()
    {
        var response = await Send(_dock, Request("interaction", "window.sync",
            """{"expanded":true,"interacting":true,"rects":[{"x":0,"y":0,"width":640,"height":88}]}"""));
        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.True(Assert.Single(_windows.Syncs).Interacting);
    }

    [Fact]
    public async Task windowSync_展开但rects为空_拒绝()
    {
        var response = await Send(_dock, Request("w2", "window.sync",
            """{"expanded":true,"rects":[]}"""));
        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Equal("INVALID_REQUEST", response.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task openSettings_合法section_转发()
    {
        foreach (var section in new[] { "projects", "appearance", "search" })
        {
            await Send(_dock, Request($"os-{section}", "window.openSettings", $$"""{"section":"{{section}}"}"""));
        }
        Assert.Equal(new[] { "projects", "appearance", "search" }, _windows.Settings.ToArray());
    }

    [Fact]
    public async Task openSettings_非法section_拒绝()
    {
        var response = await Send(_dock, Request("os-x", "window.openSettings", """{"section":"danger"}"""));
        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Empty(_windows.Settings);
    }

    [Fact]
    public async Task 每个请求_恰好一个应答()
    {
        await Send(_dock, Request("only-1", "app.getState"));
        var forThisRequest = _dock.Sent.Count(json =>
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.TryGetProperty("id", out var id) && id.GetString() == "only-1" && root.GetProperty("type").GetString() == "response";
        });
        Assert.Equal(1, forThisRequest);
    }

    [Fact]
    public async Task DropRequiresNativeFileObjectsAndDoesNotSaveOrLaunch()
    {
        var forged = await Send(_dock, Request("drop-forged", "shell.resolveDrop", """{"paths":["C:\\Windows\\notepad.exe"]}"""));
        Assert.Equal("INVALID_REQUEST", forged.GetProperty("error").GetProperty("code").GetString());
        var path = Path.Combine(_dir, "文档.txt"); File.WriteAllText(path, "fixture");
        await _router.HandleMessage(_dock, Request("drop-real", "shell.resolveDrop"), new[] { path });
        var response = JsonDocument.Parse(_dock.Sent.Last()).RootElement;
        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal("file", response.GetProperty("result")[0].GetProperty("kind").GetString());
        Assert.Empty(_store.Current.Projects);
        Assert.Empty(_shell.Launched);
        await _router.HandleMessage(_dock, Request("drop-missing", "shell.resolveDrop"), new[] { Path.Combine(_dir, "missing.txt") });
        Assert.Equal("INVALID_REQUEST", JsonDocument.Parse(_dock.Sent.Last()).RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SearchRejectsInvalidInputAndDoesNotAcceptArbitraryLaunchPath()
    {
        var query = await Send(_settings, Request("search-invalid", "search.query", """{"query":"text","scope":"disk-scan"}"""));
        Assert.Equal("INVALID_REQUEST", query.GetProperty("error").GetProperty("code").GetString());
        var open = await Send(_settings, Request("search-forged", "search.open", """{"resultId":"C:\\Windows\\notepad.exe"}"""));
        Assert.Equal("INVALID_REQUEST", open.GetProperty("error").GetProperty("code").GetString());
        var settings = await Send(_settings, Request("search-settings", "search.query", """{"query":"显示","scope":"settings"}"""));
        Assert.True(settings.GetProperty("ok").GetBoolean());
        Assert.Equal("setting", settings.GetProperty("result").GetProperty("results")[0].GetProperty("kind").GetString());
    }
}
