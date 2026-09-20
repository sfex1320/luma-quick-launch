using System.IO;
using System.Text.Json;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

[Collection("Folder IO")]
public sealed class ProjectTestBridgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "luma-project-test-bridge", Guid.NewGuid().ToString("N"));
    private readonly BridgeRouter _router;
    private readonly FakeClient _client = new("dock");
    public ProjectTestBridgeTests()
    {
        Directory.CreateDirectory(_dir);
        var store = new StateStore(Path.Combine(_dir, "config"));
        store.Load();
        store.Save(TestStates.OneProject("p", _dir), 0);
        _router = new(store, new LaunchService(store, new FakeShell()), new FakePicker(), new FakeWindows(), new ImmediateSync());
        _router.Attach(_client);
    }
    public void Dispose() { Directory.Delete(_dir, true); }
    private async Task<JsonElement> Send(string method, object args)
    {
        await _router.HandleMessage(_client, JsonSerializer.Serialize(new { protocol = 1, type = "request", id = "request", method, @params = args }));
        return JsonDocument.Parse(_client.Sent.Last()).RootElement.Clone();
    }
    [Fact]
    public async Task EmptyDirectoryDetectsNoTaskWithoutLaunching()
    {
        var listing = await Send("folder.list", new { projectId = "p", itemId = "main" });
        var folder = listing.GetProperty("result").GetProperty("folderId").GetString();
        var result = await Send("project.detectTest", new { projectId = "p", itemId = "main", folderId = folder });
        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("result").GetProperty("task").ValueKind);
    }
    [Theory]
    [InlineData("project.detectTest", "folderId")]
    [InlineData("project.runTest", "taskId")]
    public async Task ArbitraryPathOrCommandParametersAreRejected(string method, string tokenName)
    {
        var result = await Send(method, new Dictionary<string, object> { ["projectId"] = "p", ["itemId"] = "main", [tokenName] = "invented", ["command"] = "whoami" });
        Assert.Equal("INVALID_REQUEST", result.GetProperty("error").GetProperty("code").GetString());
    }
}
