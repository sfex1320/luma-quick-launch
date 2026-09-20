using System.IO;
using System.Text.Json;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

public class IconBridgeRouterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "luma-icon-tests", Guid.NewGuid().ToString("N"));
    private readonly StateStore _store;
    private readonly FakeShell _shell = new();
    private readonly FakeClient _client = new();
    private readonly BridgeRouter _router;
    public IconBridgeRouterTests()
    {
        _store = new StateStore(_dir); _store.Load();
        _router = new(_store, new LaunchService(_store, _shell), new FakePicker(), new FakeWindows(), new ImmediateSync());
    }
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
    private async Task<JsonElement> Send(object parameters)
    {
        var count = _client.Sent.Count;
        await _router.HandleMessage(_client, JsonSerializer.Serialize(new { protocol = 1, type = "request", id = Guid.NewGuid().ToString(), method = "shell.getIcon", @params = parameters }));
        Assert.Equal(count + 1, _client.Sent.Count);
        Assert.Empty(_shell.Launched);
        return JsonDocument.Parse(_client.Sent.Last()).RootElement.Clone();
    }
    [Fact]
    public async Task MissingSavedItemReturnsNullWithoutLaunching()
    {
        var response = await Send(new { projectId = "missing", itemId = "missing" });
        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("result").GetProperty("dataUrl").ValueKind);
        Assert.Equal(0, _store.Current.Revision);
    }
    [Theory]
    [InlineData("{\"projectId\":\"p\",\"itemId\":\"main\",\"path\":\"C:\\\\Windows\\\\notepad.exe\"}")]
    [InlineData("{\"projectId\":\"p\",\"itemId\":\"main\",\"size\":63}")]
    [InlineData("{\"projectId\":\"p\",\"itemId\":\"main\",\"size\":null}")]
    [InlineData("{\"projectId\":\"\",\"itemId\":\"main\"}")]
    public async Task RejectsRawPathsAndMalformedParameters(string json)
    {
        var response = await Send(JsonDocument.Parse(json).RootElement);
        Assert.Equal("INVALID_REQUEST", response.GetProperty("error").GetProperty("code").GetString());
    }
}
