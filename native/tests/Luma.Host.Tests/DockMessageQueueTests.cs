using System.Text.Json;
using Luma.Host.Windows;
using Xunit;

namespace Luma.Host.Tests;

public class DockMessageQueueTests
{
    private static string Request(string method) => JsonSerializer.Serialize(new { protocol = 1, type = "request", id = "r", method, @params = new { } });
    private static TaskCompletionSource Hold() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task NoPrepare() => Task.CompletedTask;

    [Theory]
    [InlineData("folder.list")]
    [InlineData("folder.open")]
    [InlineData("shell.getIcon")]
    [InlineData("folder.getThumbnail")]
    [InlineData("folder.getPath")]
    [InlineData("folder.createFolder")]
    [InlineData("folder.rename")]
    [InlineData("folder.move")]
    [InlineData("shell.openItem")]
    [InlineData("shell.getRecent")]
    [InlineData("shell.openRecent")]
    [InlineData("website.inspect")]
    [InlineData("search.open")]
    [InlineData("project.detectTest")]
    [InlineData("project.runTest")]
    public async Task SlowFolderDoesNotBlockWindowSyncAcknowledgement(string method)
    {
        var queue = new DockMessageQueue();
        var hold = Hold();
        var folder = queue.DispatchAsync(Request(method), NoPrepare, () => hold.Task);
        var events = new List<string>();
        var sync = queue.DispatchAsync(Request("window.sync"), () => { events.Add("dpr"); return Task.CompletedTask; }, () => { events.Add("ack"); return Task.CompletedTask; });
        try
        {
            Assert.True(sync.IsCompletedSuccessfully, "Slow folder IO must not hold the layout message queue.");
            Assert.Equal(new[] { "dpr", "ack" }, events);
            Assert.False(folder.IsCompleted);
        }
        finally { hold.SetResult(); await Task.WhenAll(folder, sync); }
    }

    [Fact]
    public async Task SavesAndWindowSyncRemainOrderedIncludingDprPreparation()
    {
        var queue = new DockMessageQueue();
        var saveHold = Hold();
        var syncHold = Hold();
        var events = new List<string>();
        var save = queue.DispatchAsync(Request("app.saveState"), NoPrepare, async () => { events.Add("save-start"); await saveHold.Task; events.Add("save-end"); });
        var first = queue.DispatchAsync(Request("window.sync"), async () => { events.Add("old-dpr"); await syncHold.Task; }, () => { events.Add("old-sync"); return Task.CompletedTask; });
        var second = queue.DispatchAsync(Request("window.sync"), () => { events.Add("new-dpr"); return Task.CompletedTask; }, () => { events.Add("new-sync"); return Task.CompletedTask; });
        Assert.Equal(new[] { "save-start" }, events);
        saveHold.SetResult();
        await save;
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        syncHold.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(new[] { "save-start", "save-end", "old-dpr", "old-sync", "new-dpr", "new-sync" }, events);
    }

    [Fact]
    public async Task CloseDropsQueuedAndNewRequests()
    {
        var queue = new DockMessageQueue();
        var hold = Hold();
        var routes = 0;
        Task Route() { routes++; return Task.CompletedTask; }
        var running = queue.DispatchAsync(Request("window.sync"), () => hold.Task, Route);
        var queued = queue.DispatchAsync(Request("app.saveState"), NoPrepare, Route);
        queue.Close();
        await queue.DispatchAsync(Request("folder.list"), NoPrepare, Route);
        hold.SetResult();
        await Task.WhenAll(running, queued);
        Assert.Equal(0, routes);
    }

    [Theory]
    [InlineData("{invalid")]
    [InlineData("{\"protocol\":\"1\",\"type\":\"request\",\"id\":\"r\",\"method\":\"folder.list\"}")]
    [InlineData("{\"protocol\":1,\"type\":\"request\",\"id\":\"r\",\"method\":false}")]
    public async Task MalformedMessageStillReachesRouterValidation(string json)
    {
        var queue = new DockMessageQueue();
        var called = false;
        await queue.DispatchAsync(json, () => throw new InvalidOperationException(), () => { called = true; return Task.CompletedTask; });
        Assert.True(called);
    }
}
