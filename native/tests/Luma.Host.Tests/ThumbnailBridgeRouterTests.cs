using System.IO;
using System.Text.Json;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

[Collection("Folder IO")]
public class ThumbnailBridgeRouterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "luma-thumbnail-bridge", Guid.NewGuid().ToString("N"));
    private readonly FakeClient _client = new();
    private readonly FakeShell _shell = new();
    private readonly StateStore _store;
    private readonly FolderService _folders;
    private readonly BridgeRouter _router;
    public ThumbnailBridgeRouterTests()
    {
        Directory.CreateDirectory(_dir); File.WriteAllText(Path.Combine(_dir, "file.psd"), "fixture");
        _store = new(Path.Combine(_dir, "config")); _store.Load();
        Assert.Equal(SaveOutcome.Saved, _store.Save(TestStates.OneProject("p", _dir), 0).Outcome);
        _folders = new(_store, _shell);
        _router = new(_store, new LaunchService(_store, _shell), new FakePicker(), new FakeWindows(), new ImmediateSync(), _folders,
            thumbnails: new FolderThumbnailService(_folders, (_, _) => null));
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
    private async Task<JsonElement> Send(string parameters)
    {
        var payload = "{\"protocol\":1,\"type\":\"request\",\"id\":\"thumbnail\",\"method\":\"folder.getThumbnail\",\"params\":" + parameters + "}";
        await _router.HandleMessage(_client, payload);
        Assert.Empty(_shell.Launched); Assert.Equal(1, _store.Current.Revision);
        return JsonDocument.Parse(_client.Sent.Last()).RootElement.Clone();
    }
    [Theory]
    [InlineData("{\"projectId\":\"p\",\"itemId\":\"main\",\"entryId\":\"e\",\"path\":\"C:\\\\test.png\"}")]
    [InlineData("{\"projectId\":\"p\",\"itemId\":\"main\",\"entryId\":\"e\",\"size\":32}")]
    [InlineData("{\"projectId\":\"p\",\"itemId\":\"main\",\"entryId\":\"e\",\"size\":null}")]
    [InlineData("{\"projectId\":\"p\",\"itemId\":\"main\",\"entryId\":\"e\",\"size\":64.5}")]
    [InlineData("{\"projectId\":\"p\",\"itemId\":\"main\",\"entryId\":\"e\",\"entryId\":\"e\"}")]
    [InlineData("{\"projectId\":\"p\",\"itemId\":\"main\"}")]
    [InlineData("{\"projectId\":\"\",\"itemId\":\"main\",\"entryId\":\"e\"}")]
    public async Task StrictParametersRejectPathsDuplicatesAndInvalidSizes(string parameters)
    {
        var response = await Send(parameters);
        Assert.Equal("INVALID_REQUEST", response.GetProperty("error").GetProperty("code").GetString());
    }
    [Theory]
    [InlineData(64)] [InlineData(96)] [InlineData(128)]
    public async Task UnsupportedProviderReturnsExplicitNullForValidToken(int size)
    {
        var listing = await _folders.ListAsync(_client.ClientId, "p", "main");
        var id = listing.Entries.Single(e => e.Name == "file.psd").Id;
        var response = await Send(JsonSerializer.Serialize(new { projectId = "p", itemId = "main", entryId = id, size }));
        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("result").GetProperty("dataUrl").ValueKind);
    }
}
