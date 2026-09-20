using System.IO;
using System.Text.Json;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

public class SystemIntegrationBridgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "luma-system-router", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("system.getIntegration", "null")]
    [InlineData("system.getIntegration", "{\"path\":\"C:\\\\Windows\"}")]
    [InlineData("system.createDesktopShortcut", "[]")]
    [InlineData("system.createDesktopShortcut", "{\"target\":\"cmd.exe\"}")]
    [InlineData("system.setAutoStart", "{}")]
    [InlineData("system.setAutoStart", "{\"enabled\":\"true\"}")]
    [InlineData("system.setAutoStart", "{\"enabled\":true,\"path\":\"cmd.exe\"}")]
    [InlineData("system.setAutoStart", "{\"enabled\":false,\"enabled\":true}")]
    public async Task MalformedParametersCannotMutateSystemEntries(string method, string parameters)
    {
        var store = new StateStore(_dir);
        store.Load();
        var router = new BridgeRouter(store, new LaunchService(store, new FakeShell()), new FakePicker(), new FakeWindows(), new ImmediateSync());
        var client = new FakeClient();
        await router.HandleMessage(client, $$"""{"protocol":1,"type":"request","id":"system","method":"{{method}}","params":{{parameters}}}""");
        using var response = JsonDocument.Parse(Assert.Single(client.Sent));
        Assert.Equal("INVALID_REQUEST", response.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, store.Current.Revision);
    }

    [Theory]
    [InlineData("system.getIntegration")]
    [InlineData("system.createDesktopShortcut")]
    [InlineData("system.setAutoStart")]
    public async Task MissingParamsObjectIsRejected(string method)
    {
        var store = new StateStore(_dir);
        store.Load();
        var router = new BridgeRouter(store, new LaunchService(store, new FakeShell()), new FakePicker(), new FakeWindows(), new ImmediateSync());
        var client = new FakeClient();
        await router.HandleMessage(client, $$"""{"protocol":1,"type":"request","id":"system","method":"{{method}}"}""");
        using var response = JsonDocument.Parse(Assert.Single(client.Sent));
        Assert.Equal("INVALID_REQUEST", response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
}
