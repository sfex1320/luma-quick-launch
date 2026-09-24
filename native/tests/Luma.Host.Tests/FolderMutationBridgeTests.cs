using System.IO;
using System.Text.Json;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

[Collection("Folder IO")]
public class FolderMutationBridgeTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "luma-mutation-bridge", Guid.NewGuid().ToString("N"));
    private readonly StateStore _store;
    private readonly FakeShell _shell = new();
    private readonly FakeClient _client = new("dock");
    private readonly FolderService _folders;
    private readonly TrackingSync _sync = new();
    private readonly BridgeRouter _router;
    private string Root => Path.Combine(_base, "root");
    private string Target => Path.Combine(_base, "target");
    public FolderMutationBridgeTests()
    {
        Directory.CreateDirectory(Root); Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Root, "one.txt"), "one"); File.WriteAllText(Path.Combine(Root, "two.txt"), "two");
        _store = new(Path.Combine(_base, "config")); _store.Load();
        var state = TestStates.OneProject("p", Root); state.Projects.Add(TestStates.OneProject("target", Target).Projects[0]);
        Assert.Equal(SaveOutcome.Saved, _store.Save(state, 0).Outcome);
        _folders = new(_store, _shell); _router = Router(_folders, _sync);
    }
    public void Dispose() { try { Directory.Delete(_base, true); } catch { } }
    private BridgeRouter Router(FolderService folders, ISyncContext sync) => new(_store, new LaunchService(_store, _shell), new FakePicker(), new FakeWindows(), sync, folders);
    private static string Request(string id, string method, object parameters) => JsonSerializer.Serialize(new { protocol = 1, type = "request", id, method, @params = parameters });
    private async Task<JsonElement> Send(string method, object parameters, BridgeRouter? router = null, FakeClient? client = null)
    {
        client ??= _client; router ??= _router;
        var id = Guid.NewGuid().ToString("N"); var before = client.Sent.Count;
        await router.HandleMessage(client, Request(id, method, parameters));
        Assert.Equal(before + 1, client.Sent.Count);
        var response = JsonDocument.Parse(client.Sent.Last()).RootElement.Clone();
        Assert.Equal(id, response.GetProperty("id").GetString()); Assert.Equal("response", response.GetProperty("type").GetString());
        Assert.Empty(_shell.Launched); Assert.Equal(1, _store.Current.Revision);
        return response;
    }
    private async Task<FolderListing> List(string project = "p", FolderService? folders = null) => await (folders ?? _folders).ListAsync("dock", project, "main");
    private static string Id(FolderListing listing, string name) => listing.Entries.Single(e => e.Name == name).Id;
    private static void Error(JsonElement response, string code) { Assert.False(response.GetProperty("ok").GetBoolean()); Assert.Equal(code, response.GetProperty("error").GetProperty("code").GetString()); }
    private static void Changed(JsonElement response, int count)
    {
        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.True(response.GetProperty("result").GetProperty("completed").GetBoolean());
        Assert.Equal(count, response.GetProperty("result").GetProperty("changedCount").GetInt32());
        Assert.False(response.GetProperty("result").TryGetProperty("ChangedCount", out _));
    }

    [Fact]
    public async Task ExternalTransferUsesAdditionalObjectsAndReturnsCommittedResult()
    {
        var target = await List("target");
        await _router.HandleMessage(_client, Request("external", "folder.transfer", new { operation = "copy", targetProject = "target", targetItem = "main", targetFolderId = target.FolderId }), [Path.Combine(Root, "one.txt")]);
        Changed(JsonDocument.Parse(_client.Sent.Last()).RootElement, 1);
        Assert.Equal("one", File.ReadAllText(Path.Combine(Target, "one.txt")));
        Assert.True(File.Exists(Path.Combine(Root, "one.txt")));
    }

    [Fact]
    public async Task InternalTransferUsesSavedIdsOrCurrentEntryToken()
    {
        var source = await List(); var target = await List("target");
        Changed(await Send("folder.transfer", new { operation = "copy", targetProject = "target", targetItem = "main", targetFolderId = target.FolderId, sourceProject = "p", sourceItem = "main", sourceEntryId = Id(source, "one.txt") }), 1);
        Assert.Equal("one", File.ReadAllText(Path.Combine(Target, "one.txt")));
        target = await List("target");
        Changed(await Send("folder.transfer", new { operation = "copy", targetProject = "target", targetItem = "main", targetFolderId = target.FolderId, sourceProject = "p", sourceItem = "main" }), 1);
        Assert.Equal("two", File.ReadAllText(Path.Combine(Target, "root", "two.txt")));
    }

    [Fact]
    public async Task TransferRejectsMixedSourcesAndJsonPathsBeforeWriting()
    {
        var target = await List("target");
        Error(await Send("folder.transfer", new { operation = "copy", targetProject = "target", targetItem = "main", targetFolderId = target.FolderId, path = Path.Combine(Root, "one.txt") }), "INVALID_REQUEST");
        await _router.HandleMessage(_client, Request("mixed", "folder.transfer", new { operation = "copy", targetProject = "target", targetItem = "main", targetFolderId = target.FolderId, sourceProject = "p", sourceItem = "main" }), [Path.Combine(Root, "one.txt")]);
        Error(JsonDocument.Parse(_client.Sent.Last()).RootElement, "INVALID_REQUEST");
        Assert.Empty(Directory.EnumerateFileSystemEntries(Target));
    }

    [Fact]
    public async Task GetPathReturnsOnlyValidatedAddressThroughBackgroundDispatch()
    {
        var listing = await List();
        var response = await Send("folder.getPath", new { projectId = "p", itemId = "main", entryId = Id(listing, "one.txt") });
        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal(Path.Combine(Root, "one.txt"), response.GetProperty("result").GetProperty("path").GetString());
        Assert.Equal(1, _sync.BackgroundCalls);
    }

    [Fact]
    public async Task CreateRenameAndMoveReturnCamelCaseResultsAndInvalidatePriorTokens()
    {
        var listing = await List();
        Changed(await Send("folder.createFolder", new { projectId = "p", itemId = "main", folderId = listing.FolderId, name = "new" }), 1);
        Assert.True(Directory.Exists(Path.Combine(Root, "new")));
        Error(await Send("folder.getPath", new { projectId = "p", itemId = "main", entryId = listing.FolderId }), "INVALID_REQUEST");
        listing = await List();
        Changed(await Send("folder.rename", new { projectId = "p", itemId = "main", entryId = Id(listing, "one.txt"), name = "renamed.txt" }), 1);
        listing = await List(); var target = await List("target");
        Changed(await Send("folder.move", new { sourceProject = "p", sourceItem = "main", sourceEntryIds = new[] { Id(listing, "renamed.txt"), Id(listing, "two.txt") }, targetProject = "target", targetItem = "main", targetFolderId = target.FolderId }), 2);
        Assert.Equal("one", File.ReadAllText(Path.Combine(Target, "renamed.txt")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(Target, "two.txt")));
        Assert.Equal(4, _sync.BackgroundCalls);
    }

    [Theory]
    [InlineData("folder.getPath", "{\"projectId\":\"p\",\"itemId\":\"main\",\"entryId\":\"e\",\"path\":\"C:\\\\Windows\"}")]
    [InlineData("folder.getPath", "{\"projectId\":\"p\",\"itemId\":\"main\",\"entryId\":\"e\",\"entryId\":\"e\"}")]
    [InlineData("folder.getPath", "{\"projectId\":\"p\",\"itemId\":\"main\",\"entryId\":false}")]
    [InlineData("folder.getPath", "{\"projectId\":\"\",\"itemId\":\"main\",\"entryId\":\"e\"}")]
    [InlineData("folder.getPath", "[]")]
    [InlineData("folder.createFolder", "{\"projectId\":\"p\",\"itemId\":\"main\",\"folderId\":\"e\"}")]
    [InlineData("folder.createFolder", "{\"projectId\":\"p\",\"itemId\":\"main\",\"folderId\":\"e\",\"name\":null}")]
    [InlineData("folder.rename", "{\"projectId\":\"p\",\"itemId\":\"main\",\"entryId\":\"e\",\"name\":\"test\",\"command\":\"cmd /c del\"}")]
    [InlineData("folder.move", "{\"sourceProject\":\"p\",\"sourceItem\":\"main\",\"sourceEntryIds\":[],\"targetProject\":\"p\",\"targetItem\":\"main\",\"targetFolderId\":\"e\"}")]
    [InlineData("folder.move", "{\"sourceProject\":\"p\",\"sourceItem\":\"main\",\"sourceEntryIds\":[\"e\",\"e\"],\"targetProject\":\"p\",\"targetItem\":\"main\",\"targetFolderId\":\"e\"}")]
    [InlineData("folder.move", "{\"sourceProject\":\"p\",\"sourceItem\":\"main\",\"sourceEntryIds\":[1],\"targetProject\":\"p\",\"targetItem\":\"main\",\"targetFolderId\":\"e\"}")]
    [InlineData("folder.move", "{\"sourceProject\":\"p\",\"sourceItem\":\"main\",\"sourceEntryIds\":\"e\",\"targetProject\":\"p\",\"targetItem\":\"main\",\"targetFolderId\":\"e\"}")]
    public async Task MalformedFieldsAreRejectedOnceBeforeScheduling(string method, string json)
    {
        Error(await Send(method, JsonDocument.Parse(json).RootElement), "INVALID_REQUEST");
        Assert.Equal(0, _sync.BackgroundCalls);
    }

    [Fact]
    public async Task OversizedIdsNamesAndArraysAreRejectedBeforeScheduling()
    {
        Error(await Send("folder.getPath", new { projectId = "p", itemId = "main", entryId = new string('x', 201) }), "INVALID_REQUEST");
        Error(await Send("folder.rename", new { projectId = "p", itemId = "main", entryId = "e", name = new string('x', 256) }), "INVALID_REQUEST");
        Error(await Send("folder.move", new { sourceProject = "p", sourceItem = "main", sourceEntryIds = Enumerable.Range(0, 101).Select(i => i.ToString()).ToArray(), targetProject = "p", targetItem = "main", targetFolderId = "e" }), "INVALID_REQUEST");
        Assert.Equal(0, _sync.BackgroundCalls);
    }

    [Fact]
    public async Task PathInjectionForeignTokensAndNoOverwriteErrorsAreSingleResponses()
    {
        var listing = await List();
        Error(await Send("folder.getPath", new { projectId = "p", itemId = "main", entryId = Root }), "INVALID_REQUEST");
        Error(await Send("folder.getPath", new { projectId = "p", itemId = "main", entryId = listing.FolderId }, client: new FakeClient("settings")), "INVALID_REQUEST");
        Error(await Send("folder.createFolder", new { projectId = "p", itemId = "main", folderId = listing.FolderId, name = "../escape" }), "INVALID_REQUEST");
        Error(await Send("folder.rename", new { projectId = "p", itemId = "main", entryId = Id(listing, "one.txt"), name = "two.txt" }), "INVALID_REQUEST");
        File.Delete(Path.Combine(Root, "one.txt"));
        Error(await Send("folder.getPath", new { projectId = "p", itemId = "main", entryId = Id(listing, "one.txt") }), "PATH_NOT_FOUND");
        Assert.Equal("two", File.ReadAllText(Path.Combine(Root, "two.txt")));
        Assert.False(Directory.Exists(Path.Combine(_base, "escape")));
    }

    [Fact]
    public async Task PartialMoveRemainsSuccessfulEnvelopeWithExplicitIncompleteResult()
    {
        var files = new InterceptFiles(); var folders = new FolderService(_store, _shell, files); var router = Router(folders, _sync);
        var listing = await List(folders: folders); var target = await List("target", folders);
        var reads = 0;
        files.AfterAttributes = path => { if (path == Path.Combine(Root, "two.txt") && ++reads == 3) File.WriteAllText(Path.Combine(Target, "two.txt"), "concurrent"); };
        var response = await Send("folder.move", new { sourceProject = "p", sourceItem = "main", sourceEntryIds = new[] { Id(listing, "one.txt"), Id(listing, "two.txt") }, targetProject = "target", targetItem = "main", targetFolderId = target.FolderId }, router);
        Assert.True(response.GetProperty("ok").GetBoolean());
        var result = response.GetProperty("result");
        Assert.False(result.GetProperty("completed").GetBoolean()); Assert.Equal(1, result.GetProperty("changedCount").GetInt32());
        Assert.Equal("INVALID_REQUEST", result.GetProperty("errorCode").GetString()); Assert.NotEmpty(result.GetProperty("message").GetString()!);
        Assert.Equal("concurrent", File.ReadAllText(Path.Combine(Target, "two.txt")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(Root, "two.txt")));
    }

    [Fact]
    public async Task AdmissionBoundsBackgroundDispatchAcrossRoutersWithoutBlockingWindowSync()
    {
        var listing = await List(); var sync = new HoldingSync();
        var router = Router(_folders, sync); var otherRouter = Router(_folders, sync);
        var first = new FakeClient("dock"); var second = new FakeClient("dock"); var third = new FakeClient("dock");
        var parameters = new { projectId = "p", itemId = "main", entryId = listing.FolderId };
        var work1 = router.HandleMessage(first, Request("one", "folder.getPath", parameters));
        var work2 = otherRouter.HandleMessage(second, Request("two", "folder.getPath", parameters));
        try
        {
            Assert.Equal(2, sync.BackgroundCalls);
            Error(await Send("folder.getPath", parameters, router, third), "BUSY");
            Assert.Equal(2, sync.BackgroundCalls);
            var layout = await Send("window.sync", new { expanded = false, rects = Array.Empty<object>(), visibilityId = 12 }, router, new FakeClient("dock"));
            Assert.True(layout.GetProperty("ok").GetBoolean()); Assert.False(work1.IsCompleted); Assert.False(work2.IsCompleted);
        }
        finally { sync.Release.SetResult(); await Task.WhenAll(work1, work2); }
        Assert.Single(first.Sent); Assert.Single(second.Sent); Assert.Single(third.Sent);
    }

    private sealed class TrackingSync : ISyncContext
    {
        public int BackgroundCalls;
        public void Post(Action action) => action();
        public Task<T> PostAsync<T>(Func<Task<T>> work) => work();
        public Task<T> RunBackground<T>(Func<Task<T>> work) { Interlocked.Increment(ref BackgroundCalls); return Task.Run(work); }
    }
    private sealed class HoldingSync : ISyncContext
    {
        public int BackgroundCalls;
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Post(Action action) => action();
        public Task<T> PostAsync<T>(Func<Task<T>> work) => work();
        public async Task<T> RunBackground<T>(Func<Task<T>> work) { Interlocked.Increment(ref BackgroundCalls); await Release.Task; return await work(); }
    }
    private sealed class InterceptFiles : IFolderFileSystem
    {
        public Action<string>? AfterAttributes;
        public FileAttributes GetAttributes(string path) { var attributes = File.GetAttributes(path); AfterAttributes?.Invoke(path); return attributes; }
        public IEnumerable<FolderDiskEntry> Enumerate(string path) => new RealFolderFileSystem().Enumerate(path);
    }
}
