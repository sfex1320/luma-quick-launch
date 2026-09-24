using System.IO;
using System.Text.Json;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

public sealed class RecentProjectBridgeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "luma-recent-bridge-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClient _client = new("dock");
    private readonly List<string> _opened = new();
    private readonly BridgeRouter _router;
    public RecentProjectBridgeTests()
    {
        var store = new StateStore(_directory); store.Load();
        var state = TestStates.OneProject("p", @"C:\Apps\Photoshop.exe"); state.Projects[0].Items[0].Kind = "app"; store.Save(state, 0);
        var source = new RecentProjectServiceTests.Source { Executable = @"C:\Apps\Photoshop.exe" };
        source.Rows.Add(new(@"C:\Art\example.psd", DateTimeOffset.UtcNow)); source.Files.Add(@"C:\Art\example.psd");
        var recent = new RecentProjectService(store, source, (path, cancel) => { cancel.ThrowIfCancellationRequested(); _opened.Add(path); return null; });
        _router = new(store, new LaunchService(store, new FakeShell()), new FakePicker(), new FakeWindows(), new ImmediateSync(), recentProjects: recent);
        _router.Attach(_client);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private async Task<JsonElement> Send(string method, object parameters)
    {
        var before = _client.Sent.Count;
        await _router.HandleMessage(_client, JsonSerializer.Serialize(new { protocol = 1, type = "request", id = Guid.NewGuid().ToString("N"), method, @params = parameters }));
        Assert.Equal(before + 1, _client.Sent.Count);
        return JsonDocument.Parse(_client.Sent.Last()).RootElement.Clone();
    }
    [Fact]
    public async Task CapabilitiesOnlyAcceptSavedIdentity()
    {
        var response = await Send("shell.getAppCapabilities", new { projectId = "p", itemId = "main" });
        Assert.True(response.GetProperty("result").GetProperty("recentSupported").GetBoolean());
        response = await Send("shell.getAppCapabilities", new { projectId = "p", itemId = "main", path = @"C:\Apps\Other.exe" });
        Assert.Equal("INVALID_REQUEST", response.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_opened);
    }

    [Fact]
    public async Task GetAndOpenUseOnlySavedItemAndIssuedCapability()
    {
        var result = await Send("shell.getRecent", new { projectId = "p", itemId = "main", limit = 6 });
        Assert.True(result.GetProperty("ok").GetBoolean());
        var listing = result.GetProperty("result");
        Assert.Equal("file", listing.GetProperty("entries")[0].GetProperty("kind").GetString());
        Assert.NotEmpty(listing.GetProperty("note").GetString()!);
        Assert.Empty(_opened);
        var id = listing.GetProperty("entries")[0].GetProperty("id").GetString();
        result = await Send("shell.openRecent", new { projectId = "p", itemId = "main", entryId = id });
        Assert.True(result.GetProperty("result").GetProperty("accepted").GetBoolean());
        Assert.Equal(@"C:\Art\example.psd", Assert.Single(_opened));
        _router.Detach(_client);
        result = await Send("shell.openRecent", new { projectId = "p", itemId = "main", entryId = id });
        Assert.Equal("INVALID_REQUEST", result.GetProperty("error").GetProperty("code").GetString());
        Assert.Single(_opened);
    }
    [Theory]
    [InlineData("shell.getRecent", "{\"projectId\":\"p\",\"itemId\":\"main\",\"limit\":5}")]
    [InlineData("shell.getRecent", "{\"projectId\":\"p\",\"itemId\":\"main\",\"limit\":11}")]
    [InlineData("shell.getRecent", "{\"projectId\":\"p\",\"itemId\":\"main\",\"limit\":6.5}")]
    [InlineData("shell.getRecent", "{\"projectId\":\"p\",\"itemId\":\"main\",\"limit\":6,\"path\":\"C:\\\\secret.psd\"}")]
    [InlineData("shell.getRecent", "{\"projectId\":\"p\",\"itemId\":\"main\",\"limit\":6,\"limit\":6}")]
    [InlineData("shell.getRecent", "{\"projectId\":\"\",\"itemId\":\"main\",\"limit\":6}")]
    [InlineData("shell.openRecent", "{\"projectId\":\"p\",\"itemId\":\"main\",\"entryId\":7}")]
    [InlineData("shell.openRecent", "{\"projectId\":\"p\",\"itemId\":\"main\",\"entryId\":\"C:\\\\secret.psd\"}")]
    [InlineData("shell.openRecent", "{\"projectId\":\"p\",\"itemId\":\"main\",\"entryId\":\"token\",\"command\":\"run\"}")]
    public async Task RejectsPathsCommandsInvalidLimitsAndDuplicates(string method, string json)
    {
        var response = await Send(method, JsonDocument.Parse(json).RootElement);
        Assert.Equal("INVALID_REQUEST", response.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_opened);
    }
}
