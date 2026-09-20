using System.IO;
using System.Text.Json;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

[Collection("Folder IO")]
public class FolderBridgeRouterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "luma-folder-router", Guid.NewGuid().ToString("N"));
    private readonly StateStore _store;
    private readonly FakeShell _shell = new();
    private readonly FakeWindows _windows = new();
    private readonly FakeClient _dock = new("dock");
    private readonly FolderService _folders;
    private readonly BridgeRouter _router;

    public FolderBridgeRouterTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "文档.txt"), "fixture");
        _store = new StateStore(Path.Combine(_dir, "config"));
        _store.Load();
        Assert.Equal(SaveOutcome.Saved, _store.Save(TestStates.OneProject("p", _dir), 0).Outcome);
        _folders = new FolderService(_store, _shell);
        _router = Router(new ImmediateSync());
        _router.Attach(_dock);
    }
    private BridgeRouter Router(ISyncContext sync) => new(_store, new LaunchService(_store, _shell), new FakePicker(), _windows, sync, _folders);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
    private static string Request(string id, string method, object parameters) => JsonSerializer.Serialize(new { protocol = 1, type = "request", id, method, @params = parameters });
    private async Task<JsonElement> Send(string id, string method, object parameters, FakeClient? client = null)
    {
        client ??= _dock;
        var before = client.Sent.Count;
        await _router.HandleMessage(client, Request(id, method, parameters));
        Assert.Equal(before + 1, client.Sent.Count);
        return JsonDocument.Parse(client.Sent.Last()).RootElement.Clone();
    }

    [Fact]
    public async Task FolderOpenUsesIssuedIdsAndDuplicateRequestsLaunchExactlyOnce()
    {
        var list = await Send("list", "folder.list", new { projectId = "p", itemId = "main" });
        var fileId = list.GetProperty("result").GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "文档.txt").GetProperty("id").GetString();
        Assert.Empty(_shell.Launched);
        var first = await Send("open", "folder.open", new { projectId = "p", itemId = "main", entryId = fileId });
        var second = await Send("open", "folder.open", new { projectId = "p", itemId = "main", entryId = fileId });
        Assert.True(first.GetProperty("result").GetProperty("accepted").GetBoolean());
        Assert.True(second.GetProperty("result").GetProperty("accepted").GetBoolean());
        Assert.Equal(Path.Combine(_dir, "文档.txt"), Assert.Single(_shell.Launched));
    }

    [Theory]
    [InlineData("folder.list", "{\"projectId\":\"p\",\"itemId\":\"main\",\"folderId\":null}")]
    [InlineData("folder.list", "{\"projectId\":\"p\",\"itemId\":\"main\",\"path\":\"C:\\\\Windows\"}")]
    [InlineData("folder.open", "{\"projectId\":\"p\",\"itemId\":\"main\"}")]
    [InlineData("folder.open", "{\"projectId\":\"p\",\"itemId\":\"main\",\"entryId\":false}")]
    [InlineData("folder.list", "{\"projectId\":\"\",\"itemId\":\"main\"}")]
    public async Task MalformedParametersAreRejectedOnce(string method, string parameters)
    {
        var response = await Send("invalid", method, JsonDocument.Parse(parameters).RootElement);
        Assert.Equal("INVALID_REQUEST", response.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_shell.Launched);
    }

    [Fact]
    public async Task TokenCannotBeReusedByAnotherWindow()
    {
        var list = await Send("list", "folder.list", new { projectId = "p", itemId = "main" });
        var id = list.GetProperty("result").GetProperty("folderId").GetString();
        var response = await Send("open", "folder.open", new { projectId = "p", itemId = "main", entryId = id }, new FakeClient("settings"));
        Assert.Equal("INVALID_REQUEST", response.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_shell.Launched);
    }

    [Fact]
    public async Task WindowSyncAcknowledgesOnlyAfterWindowApplied()
    {
        var sync = new DeferredSync();
        var router = Router(sync);
        await router.HandleMessage(_dock, Request("sync", "window.sync", new { expanded = false, visibilityId = 42, rects = Array.Empty<object>() }));
        Assert.Empty(_dock.Sent);
        Assert.Empty(_windows.Syncs);
        Assert.Single(sync.Pending)();
        Assert.Equal(42, Assert.Single(_windows.Syncs).VisibilityId);
        var response = JsonDocument.Parse(Assert.Single(_dock.Sent)).RootElement;
        Assert.True(response.GetProperty("result").GetProperty("applied").GetBoolean());
    }

    private sealed class DeferredSync : ISyncContext
    {
        public List<Action> Pending { get; } = new();
        public void Post(Action action) => Pending.Add(action);
        public Task<T> RunBackground<T>(Func<Task<T>> work) => work();
        public Task<T> PostAsync<T>(Func<Task<T>> work) => work();
    }
}
