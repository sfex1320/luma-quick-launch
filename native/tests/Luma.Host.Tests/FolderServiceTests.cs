using System.IO;
using System.Text.Json;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

[CollectionDefinition("Folder IO", DisableParallelization = true)]
public class FolderIoCollection { }

[Collection("Folder IO")]
public class FolderServiceTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "luma-folder-tests", Guid.NewGuid().ToString("N"));
    private readonly StateStore _store;
    private readonly FakeShell _shell = new();
    private readonly FolderService _folders;
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private string Root => Path.Combine(_base, "电梯贴");

    public FolderServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(Root, "目录2", "内层"));
        Directory.CreateDirectory(Path.Combine(Root, "目录10"));
        File.WriteAllText(Path.Combine(Root, "方案.pptx"), "fixture");
        _store = new StateStore(Path.Combine(_base, "config"));
        _store.Load();
        Assert.Equal(SaveOutcome.Saved, _store.Save(TestStates.OneProject("p", Root), 0).Outcome);
        _folders = new FolderService(_store, _shell, clock: () => _now);
    }

    public void Dispose() { try { Directory.Delete(_base, true); } catch { } }
    private Task<FolderListing> List(string? folder = null) => _folders.ListAsync("dock", "p", "main", folder);
    private Task<LaunchOutcome> Open(string token, string? request = null) => _folders.OpenAsync("dock", request ?? Guid.NewGuid().ToString("N"), "p", "main", token);

    [Fact]
    public async Task ListsOneLevelNaturallyAndNeverSavesOrLaunches()
    {
        var listing = await List();
        Assert.Equal(new[] { "目录2", "目录10", "方案.pptx" }, listing.Entries.Select(e => e.Name));
        Assert.Equal(new[] { "folder", "folder", "file" }, listing.Entries.Select(e => e.Kind));
        Assert.Null(listing.ParentId);
        Assert.False(listing.Truncated);
        Assert.Equal("电梯贴", listing.Name);
        Assert.Empty(_shell.Launched);
        Assert.Equal(1, _store.Current.Revision);
        Assert.DoesNotContain(Root, JsonSerializer.Serialize(listing, ContractsJson.Options));
    }

    [Fact]
    public async Task NestedNavigationReturnsUsableParentAndCurrentDirectoryToken()
    {
        var root = await List();
        var child = await List(root.Entries[0].Id);
        Assert.Equal(root.Entries[0].Id, child.FolderId);
        Assert.Equal(root.FolderId, child.ParentId);
        Assert.Equal("内层", Assert.Single(child.Entries).Name);
        var grandchild = await List(child.Entries[0].Id);
        Assert.Equal(child.FolderId, grandchild.ParentId);
        Assert.Empty(grandchild.Entries);
        Assert.Equal(root.Entries, (await List(child.ParentId)).Entries);
        Assert.True((await Open(child.FolderId)).Accepted);
        Assert.Equal(Path.Combine(Root, "目录2"), Assert.Single(_shell.Launched));
    }

    [Fact]
    public async Task RequestIdDeduplicationLaunchesOnceIncludingConcurrentCalls()
    {
        var listing = await List();
        var file = listing.Entries.Single(e => e.Kind == "file");
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Open(file.Id, "same-request")));
        Assert.All(results, result => Assert.True(result.Accepted));
        Assert.Equal(Path.Combine(Root, "方案.pptx"), Assert.Single(_shell.Launched));
    }

    [Fact]
    public async Task TokensRejectWrongClientProjectItemAndRawPaths()
    {
        var root = await List();
        var state = _store.Current;
        state.Projects.Add(TestStates.OneProject("other", Root).Projects[0]);
        state.Projects[0].Items.Add(new LaunchItem { Id = "other", Name = "other", Kind = "folder", Path = Root });
        Assert.Equal(SaveOutcome.Saved, _store.Save(state, state.Revision).Outcome);
        await Error("INVALID_REQUEST", () => _folders.ListAsync("settings", "p", "main", root.FolderId));
        await Error("INVALID_REQUEST", () => _folders.ListAsync("dock", "other", "main", root.FolderId));
        await Error("INVALID_REQUEST", () => _folders.ListAsync("dock", "p", "other", root.FolderId));
        await Error("INVALID_REQUEST", () => Open(Root));
        await Error("INVALID_REQUEST", () => List(Path.Combine(Root, "..")));
        await Error("INVALID_REQUEST", () => _folders.OpenAsync("settings", "open", "p", "main", root.FolderId));
        Assert.Empty(_shell.Launched);
    }

    [Fact]
    public async Task ChangedOrDeletedSavedRootInvalidatesOldTokens()
    {
        var root = await List();
        var state = _store.Current;
        state.Projects[0].Items[0].Path = Path.Combine(Root, "目录2");
        Assert.Equal(SaveOutcome.Saved, _store.Save(state, state.Revision).Outcome);
        await Error("INVALID_REQUEST", () => List(root.FolderId));
        await Error("INVALID_REQUEST", () => Open(root.Entries[0].Id));
        state = _store.Current;
        state.Projects.Clear();
        Assert.Equal(SaveOutcome.Saved, _store.Save(state, state.Revision).Outcome);
        await Error("PATH_NOT_FOUND", () => Open(root.FolderId));
        Assert.Empty(_shell.Launched);
    }

    [Fact]
    public async Task ExpiredTokensAndDetachedClientsMustRelist()
    {
        var root = await List();
        _now = _now.AddMinutes(5);
        await Error("INVALID_REQUEST", () => List(root.FolderId));
        await Error("INVALID_REQUEST", () => Open(root.Entries[0].Id));
        var fresh = await List();
        Assert.NotEqual(root.FolderId, fresh.FolderId);
        _folders.Detach("dock");
        await Error("INVALID_REQUEST", () => List(fresh.FolderId));
    }

    [Fact]
    public async Task DeletedFilesAndNonFoldersProduceLocalizedErrors()
    {
        var root = await List();
        var file = root.Entries.Single(e => e.Kind == "file");
        await Error("INVALID_REQUEST", () => List(file.Id));
        File.Delete(Path.Combine(Root, "方案.pptx"));
        await Error("PATH_NOT_FOUND", () => Open(file.Id));
        Directory.Delete(Path.Combine(Root, "目录10"));
        await Error("PATH_NOT_FOUND", () => List(root.Entries[1].Id));
        Assert.Empty(_shell.Launched);
    }

    [Fact]
    public async Task NonFolderSavedItemIsRejected()
    {
        var state = _store.Current;
        state.Projects[0].Items[0].Kind = "file";
        Assert.Equal(SaveOutcome.Saved, _store.Save(state, state.Revision).Outcome);
        await Error("INVALID_REQUEST", () => List());
    }

    [Fact]
    public async Task ListsAtMost200AndReadsOnly201Records()
    {
        var files = new CountingFiles();
        var folders = new FolderService(_store, _shell, files);
        var listing = await folders.ListAsync("dock", "p", "main");
        Assert.Equal(200, listing.Entries.Count);
        Assert.True(listing.Truncated);
        Assert.Equal(201, files.ReadCount);
        Assert.Empty(_shell.Launched);
    }

    [Fact]
    public async Task RealLargeFolderIsTruncated()
    {
        for (var i = 0; i < 205; i++) File.WriteAllText(Path.Combine(Root, $"file{i}.txt"), "");
        var listing = await List();
        Assert.Equal(200, listing.Entries.Count);
        Assert.True(listing.Truncated);
    }

    [Fact]
    public async Task PermissionAndIoFailuresAreLocalized()
    {
        var denied = new FolderService(_store, _shell, new ErrorFiles(new UnauthorizedAccessException("secret")));
        await Error("ACCESS_DENIED", () => denied.ListAsync("dock", "p", "main"));
        var io = new FolderService(_store, _shell, new ErrorFiles(new IOException("secret")));
        await Error("IO_ERROR", () => io.ListAsync("dock", "p", "main"));
    }

    [Fact]
    public async Task ReparseSubstitutionAndShellLinksCannotEscapeRoot()
    {
        var files = new ReparseFiles();
        var folders = new FolderService(_store, _shell, files);
        var listing = await folders.ListAsync("dock", "p", "main");
        files.BlockedPath = Path.Combine(Root, "目录2");
        await Error("ACCESS_DENIED", () => folders.ListAsync("dock", "p", "main", listing.Entries[0].Id));
        await Error("ACCESS_DENIED", () => folders.OpenAsync("dock", "reparse", "p", "main", listing.Entries[0].Id));
        File.WriteAllText(Path.Combine(Root, "外部.lnk"), "fixture");
        var fresh = await List();
        await Error("ACCESS_DENIED", () => Open(fresh.Entries.Single(e => e.Name == "外部.lnk").Id));
        Assert.Empty(_shell.Launched);
    }

    [Fact]
    public async Task ReparseReplacementDuringEnumerationDoesNotReturnEscapedContents()
    {
        var files = new MutatingFiles();
        var folders = new FolderService(_store, _shell, files);
        await Error("ACCESS_DENIED", () => folders.ListAsync("dock", "p", "main"));
        Assert.Empty(_shell.Launched);
    }

    [Fact]
    public async Task TokenCacheEvictsOldPagesButKeepsEveryCurrentPageTokenUsable()
    {
        var files = new CountingFiles();
        var folders = new FolderService(_store, _shell, files);
        var old = await folders.ListAsync("old", "p", "main");
        FolderListing? latest = null;
        for (var i = 0; i < 22; i++) latest = await folders.ListAsync($"client{i}", "p", "main");
        await Error("INVALID_REQUEST", () => folders.OpenAsync("old", "old-open", "p", "main", old.FolderId));
        Assert.NotNull(latest);
        Assert.True((await folders.OpenAsync("client21", "root-open", "p", "main", latest.FolderId)).Accepted);
        foreach (var entry in latest.Entries)
            Assert.True((await folders.OpenAsync("client21", entry.Id, "p", "main", entry.Id)).Accepted);
        Assert.Equal(201, _shell.LaunchCount);
    }

    [Fact]
    public async Task DriveRootAllowsChildTokenWithoutDoubleSeparatorBoundaryFailure()
    {
        var state = _store.Current;
        var driveRoot = Path.GetPathRoot(Root)!;
        state.Projects[0].Items[0].Path = driveRoot;
        Assert.Equal(SaveOutcome.Saved, _store.Save(state, state.Revision).Outcome);
        var files = new CountingFiles();
        var folders = new FolderService(_store, _shell, files);
        var root = await folders.ListAsync("dock", "p", "main");
        Assert.True((await folders.OpenAsync("dock", "drive-child", "p", "main", root.Entries[0].Id)).Accepted);
        Assert.Single(_shell.Launched);
    }

    [Fact]
    public async Task TimeoutRetainsWorkerSlotsUntilActualIoEnds()
    {
        using var unblock = new ManualResetEventSlim();
        using var started = new CountdownEvent(2);
        using var ended = new CountdownEvent(2);
        var files = new BlockingFiles(unblock, started, ended);
        var folders = new FolderService(_store, _shell, files, timeout: TimeSpan.FromMilliseconds(100));
        var first = folders.ListAsync("dock", "p", "main");
        var second = folders.ListAsync("settings", "p", "main");
        Assert.True(started.Wait(TimeSpan.FromSeconds(3)));
        try
        {
            await Error("BUSY", () => first);
            await Error("BUSY", () => second);
            await Error("BUSY", () => folders.ListAsync("third", "p", "main"));
            Assert.Equal(2, files.ReadCount);
        }
        finally
        {
            unblock.Set();
            Assert.True(ended.Wait(TimeSpan.FromSeconds(3)));
            // Await real worker finally blocks rather than leaving hung IO behind for other tests.
            for (var attempts = 0; attempts < 100; attempts++)
            {
                try { await List(); break; }
                catch (FolderOperationException ex) when (ex.Code == "BUSY" && attempts < 99) { await Task.Delay(10); }
            }
        }
        Assert.Empty(_shell.Launched);
    }

    private static async Task Error(string code, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<FolderOperationException>(action);
        Assert.Equal(code, error.Code);
        Assert.NotEmpty(error.Message);
        Assert.DoesNotContain("secret", error.Message);
    }

    private class CountingFiles : IFolderFileSystem
    {
        public int ReadCount;
        public FileAttributes GetAttributes(string path) => FileAttributes.Directory;
        public IEnumerable<FolderDiskEntry> Enumerate(string path)
        {
            for (var i = 0; i < 1000; i++)
            {
                ReadCount++;
                yield return new(Path.Combine(path, $"目录{i}"), $"目录{i}", true);
            }
        }
    }
    private sealed class ErrorFiles(Exception error) : IFolderFileSystem
    {
        public FileAttributes GetAttributes(string path) => throw error;
        public IEnumerable<FolderDiskEntry> Enumerate(string path) => throw error;
    }
    private sealed class ReparseFiles : IFolderFileSystem
    {
        public string? BlockedPath;
        private readonly RealFolderFileSystem _real = new();
        public FileAttributes GetAttributes(string path) => _real.GetAttributes(path) | (path == BlockedPath ? FileAttributes.ReparsePoint : 0);
        public IEnumerable<FolderDiskEntry> Enumerate(string path) => _real.Enumerate(path);
    }
    private sealed class MutatingFiles : IFolderFileSystem
    {
        private bool _changed;
        public FileAttributes GetAttributes(string path) => FileAttributes.Directory | (_changed ? FileAttributes.ReparsePoint : 0);
        public IEnumerable<FolderDiskEntry> Enumerate(string path)
        {
            _changed = true;
            yield return new(Path.Combine(path, "outside"), "outside", true);
        }
    }
    private sealed class BlockingFiles(ManualResetEventSlim unblock, CountdownEvent started, CountdownEvent ended) : IFolderFileSystem
    {
        public int ReadCount;
        public FileAttributes GetAttributes(string path) => FileAttributes.Directory;
        public IEnumerable<FolderDiskEntry> Enumerate(string path)
        {
            Interlocked.Increment(ref ReadCount);
            started.Signal();
            try { unblock.Wait(); yield return new(Path.Combine(path, "entry"), "entry", true); }
            finally { ended.Signal(); }
        }
    }
}
