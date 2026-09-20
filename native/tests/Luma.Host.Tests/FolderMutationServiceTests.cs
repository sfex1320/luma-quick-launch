using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Luma.Host.Tests;

[Collection("Folder IO")]
public class FolderMutationServiceTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "luma-mutation-tests", Guid.NewGuid().ToString("N"));
    private readonly StateStore _store;
    private readonly FakeShell _shell = new();
    private readonly FolderService _folders;
    private readonly FolderMutationService _mutations;
    private string Root => Path.Combine(_base, "source");
    private string Target => Path.Combine(_base, "target");
    public FolderMutationServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(Root, "child", "inside"));
        Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Root, "one.txt"), "one");
        File.WriteAllText(Path.Combine(Root, "two.txt"), "two");
        _store = new(Path.Combine(_base, "config")); _store.Load();
        var state = TestStates.OneProject("p", Root);
        state.Projects.Add(TestStates.OneProject("target", Target).Projects[0]);
        Assert.Equal(SaveOutcome.Saved, _store.Save(state, 0).Outcome);
        _folders = new(_store, _shell); _mutations = new(_folders);
    }
    public void Dispose() { try { Directory.Delete(_base, true); } catch { } }
    private Task<FolderListing> List(string project = "p", string client = "dock", string? folder = null) => _folders.ListAsync(client, project, "main", folder);
    private static string Id(FolderListing listing, string name) => listing.Entries.Single(e => e.Name == name).Id;
    private static async Task Error(string code, Func<Task> action) => Assert.Equal(code, (await Assert.ThrowsAsync<FolderOperationException>(action)).Code);

    [Fact]
    public async Task GetPathOnlyReturnsValidatedEntryAndDoesNotLaunchOrSave()
    {
        var listing = await List();
        Assert.Equal(Root, await _mutations.GetPathAsync("dock", "p", "main", listing.FolderId));
        Assert.Equal(Path.Combine(Root, "one.txt"), await _mutations.GetPathAsync("dock", "p", "main", Id(listing, "one.txt")));
        Assert.Empty(_shell.Launched); Assert.Equal(1, _store.Current.Revision);
        Assert.Equal("one", File.ReadAllText(Path.Combine(Root, "one.txt")));
    }

    [Fact]
    public async Task CreateFolderUsesRealDirectoryAndInvalidatesEveryClientToken()
    {
        var listing = await List(); var settings = await List(client: "settings");
        var result = await _mutations.CreateFolderAsync("dock", "p", "main", listing.FolderId, "新建文件夹");
        Assert.True(result.Completed); Assert.Equal(1, result.ChangedCount);
        Assert.True(Directory.Exists(Path.Combine(Root, "新建文件夹")));
        await Error("INVALID_REQUEST", () => _mutations.GetPathAsync("dock", "p", "main", listing.FolderId));
        await Error("INVALID_REQUEST", () => _mutations.GetPathAsync("settings", "p", "main", settings.FolderId));
        Assert.Contains((await List()).Entries, entry => entry.Name == "新建文件夹");
        Assert.Empty(_shell.Launched); Assert.Equal(1, _store.Current.Revision);
    }

    [Theory]
    [InlineData("one.txt", "renamed.txt", false)]
    [InlineData("child", "renamed-folder", true)]
    [InlineData("one.txt", "rock && roll.txt", false)]
    public async Task RenameChangesOnlyTheSelectedRealEntry(string before, string after, bool directory)
    {
        var listing = await List();
        var result = await _mutations.RenameAsync("dock", "p", "main", Id(listing, before), after);
        Assert.True(result.Completed); Assert.Equal(1, result.ChangedCount);
        Assert.False(File.Exists(Path.Combine(Root, before)) || Directory.Exists(Path.Combine(Root, before)));
        if (directory) Assert.True(Directory.Exists(Path.Combine(Root, after, "inside")));
        else Assert.Equal("one", File.ReadAllText(Path.Combine(Root, after)));
        Assert.Equal("two", File.ReadAllText(Path.Combine(Root, "two.txt")));
        Assert.Equal(1, _store.Current.Revision);
    }

    [Fact]
    public async Task MoveCanCrossTwoExplicitSavedRootsOnSameVolumeWithoutCopying()
    {
        var source = await List(); var target = await List("target");
        var result = await _mutations.MoveAsync("dock", "p", "main", [Id(source, "one.txt"), Id(source, "child")], "target", "main", target.FolderId);
        Assert.True(result.Completed); Assert.Equal(2, result.ChangedCount);
        Assert.False(File.Exists(Path.Combine(Root, "one.txt"))); Assert.False(Directory.Exists(Path.Combine(Root, "child")));
        Assert.Equal("one", File.ReadAllText(Path.Combine(Target, "one.txt")));
        Assert.True(Directory.Exists(Path.Combine(Target, "child", "inside")));
        await Error("INVALID_REQUEST", () => _mutations.GetPathAsync("dock", "target", "main", target.FolderId));
        Assert.Equal(1, _store.Current.Revision); Assert.Empty(_shell.Launched);
    }

    [Theory]
    [InlineData("")][InlineData(" ")][InlineData(".")][InlineData("..")][InlineData("../outside")][InlineData("child\\outside")]
    [InlineData("C:\\outside")][InlineData("file:stream")][InlineData("trailing.")][InlineData("trailing ")][InlineData("bad\nname")]
    [InlineData("NUL")][InlineData("con.txt")][InlineData("CON .txt")][InlineData("LPT9.log")][InlineData("COM¹.txt")][InlineData("CONOUT$")]
    [InlineData("bad*name")][InlineData("bad?name")][InlineData("bad|name")]
    public async Task IllegalNamesCannotCreateOrRename(string name)
    {
        var listing = await List();
        await Error("INVALID_REQUEST", () => _mutations.CreateFolderAsync("dock", "p", "main", listing.FolderId, name));
        await Error("INVALID_REQUEST", () => _mutations.RenameAsync("dock", "p", "main", Id(listing, "one.txt"), name));
        Assert.Equal("one", File.ReadAllText(Path.Combine(Root, "one.txt")));
        Assert.Equal(3, (await List()).Entries.Count);
    }

    [Fact]
    public async Task ExistingFilesAndDirectoriesAreNeverOverwrittenAndBatchPreflightIsAllOrNothing()
    {
        File.WriteAllText(Path.Combine(Target, "two.txt"), "target data");
        var source = await List(); var target = await List("target");
        await Error("INVALID_REQUEST", () => _mutations.CreateFolderAsync("dock", "p", "main", source.FolderId, "one.txt"));
        await Error("INVALID_REQUEST", () => _mutations.CreateFolderAsync("dock", "p", "main", source.FolderId, "child"));
        await Error("INVALID_REQUEST", () => _mutations.RenameAsync("dock", "p", "main", Id(source, "one.txt"), "two.txt"));
        await Error("INVALID_REQUEST", () => _mutations.MoveAsync("dock", "p", "main", [Id(source, "one.txt"), Id(source, "two.txt")], "target", "main", target.FolderId));
        Assert.Equal("one", File.ReadAllText(Path.Combine(Root, "one.txt")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(Root, "two.txt")));
        Assert.Equal("target data", File.ReadAllText(Path.Combine(Target, "two.txt")));
        Assert.False(File.Exists(Path.Combine(Target, "one.txt")));
    }

    [Fact]
    public async Task RootMovesAndMovingIntoDescendantsAreRejected()
    {
        var source = await List(); var target = await List("target");
        var child = await List(folder: Id(source, "child"));
        await Error("INVALID_REQUEST", () => _mutations.RenameAsync("dock", "p", "main", source.FolderId, "renamed"));
        await Error("INVALID_REQUEST", () => _mutations.MoveAsync("dock", "p", "main", [source.FolderId], "target", "main", target.FolderId));
        await Error("INVALID_REQUEST", () => _mutations.MoveAsync("dock", "p", "main", [child.FolderId], "p", "main", Id(child, "inside")));
        await Error("INVALID_REQUEST", () => _mutations.MoveAsync("dock", "p", "main", [child.FolderId, Id(child, "inside")], "target", "main", target.FolderId));
        Assert.True(Directory.Exists(Path.Combine(Root, "child", "inside")));
    }

    [Fact]
    public async Task ForeignClientRawPathChangedRootAndDetachedTokensAreRejected()
    {
        var source = await List(); var target = await List("target", client: "settings");
        await Error("INVALID_REQUEST", () => _mutations.GetPathAsync("settings", "p", "main", source.FolderId));
        await Error("INVALID_REQUEST", () => _mutations.GetPathAsync("dock", "p", "main", Root));
        await Error("INVALID_REQUEST", () => _mutations.MoveAsync("dock", "p", "main", [Id(source, "one.txt")], "target", "main", target.FolderId));
        var state = _store.Current; state.Projects[0].Items[0].Path = Path.Combine(Root, "child");
        Assert.Equal(SaveOutcome.Saved, _store.Save(state, state.Revision).Outcome);
        await Error("INVALID_REQUEST", () => _mutations.CreateFolderAsync("dock", "p", "main", source.FolderId, "new"));
        var current = await List(); _folders.Detach("dock");
        await Error("INVALID_REQUEST", () => _mutations.CreateFolderAsync("dock", "p", "main", current.FolderId, "new"));
        Assert.False(Directory.Exists(Path.Combine(Root, "new")));
    }

    [Fact]
    public async Task NativeHandleValidationRejectsJunctionIntroducedAfterCapabilityValidation()
    {
        var victim = Path.Combine(Root, "victim"); Directory.CreateDirectory(victim);
        var outside = Path.Combine(_base, "outside"); Directory.CreateDirectory(outside);
        var files = new InterceptFiles();
        var folders = new FolderService(_store, _shell, files);
        var listing = await folders.ListAsync("dock", "p", "main");
        var reads = 0;
        files.AfterAttributes = path =>
        {
            // First capability validation sees ordinary attributes; substitute a junction before native pinning.
            if (path == victim && ++reads == 2) CreateJunction(victim, outside);
        };
        var service = new FolderMutationService(folders);
        await Error("ACCESS_DENIED", () => service.CreateFolderAsync("dock", "p", "main", Id(listing, "victim"), "escape"));
        Assert.False(Directory.Exists(Path.Combine(outside, "escape")));
        Assert.Equal(FileAttributes.ReparsePoint, File.GetAttributes(victim) & FileAttributes.ReparsePoint);
    }

    [Fact]
    public async Task PinnedAncestorsPreventRenameSubstitutionDuringFinalValidation()
    {
        var victim = Path.Combine(Root, "victim"); Directory.CreateDirectory(victim);
        var moved = Path.Combine(_base, "moved");
        var files = new InterceptFiles(); var folders = new FolderService(_store, _shell, files);
        var listing = await folders.ListAsync("dock", "p", "main");
        var reads = 0; Exception? attempt = null;
        files.AfterAttributes = path =>
        {
            if (path != victim || ++reads != 3) return;
            try { Directory.Move(victim, moved); }
            catch (Exception ex) { attempt = ex; }
        };
        var result = await new FolderMutationService(folders).CreateFolderAsync("dock", "p", "main", Id(listing, "victim"), "safe");
        Assert.True(result.Completed);
        Assert.IsType<IOException>(attempt);
        Assert.True(Directory.Exists(Path.Combine(victim, "safe"))); Assert.False(Directory.Exists(moved));
    }

    [Fact]
    public async Task FinalNativeVerificationRejectsReparseChangeDuringAuthorityRecheck()
    {
        var victim = Path.Combine(Root, "victim"); Directory.CreateDirectory(victim);
        var outside = Path.Combine(_base, "outside"); Directory.CreateDirectory(outside);
        var files = new InterceptFiles(); var folders = new FolderService(_store, _shell, files);
        var listing = await folders.ListAsync("dock", "p", "main");
        var reads = 0;
        files.AfterAttributes = path =>
        {
            if (path != victim || ++reads != 3) return;
            CreateJunction(victim, outside);
        };
        await Error("ACCESS_DENIED", () => new FolderMutationService(folders).CreateFolderAsync("dock", "p", "main", Id(listing, "victim"), "safe"));
        Assert.False(Directory.Exists(Path.Combine(outside, "safe")));
    }

    [Fact]
    public async Task DestinationCreatedAfterPreflightProducesExplicitPartialResultWithoutOverwrite()
    {
        var files = new InterceptFiles(); var folders = new FolderService(_store, _shell, files);
        var listing = await folders.ListAsync("dock", "p", "main");
        var target = await folders.ListAsync("dock", "target", "main");
        var reads = 0;
        files.AfterAttributes = path =>
        {
            if (path == Path.Combine(Root, "two.txt") && ++reads == 3)
                File.WriteAllText(Path.Combine(Target, "two.txt"), "concurrent target");
        };
        var result = await new FolderMutationService(folders).MoveAsync("dock", "p", "main", [Id(listing, "one.txt"), Id(listing, "two.txt")], "target", "main", target.FolderId);
        Assert.False(result.Completed); Assert.Equal(1, result.ChangedCount); Assert.Equal("INVALID_REQUEST", result.ErrorCode);
        Assert.Equal("one", File.ReadAllText(Path.Combine(Target, "one.txt"))); Assert.False(File.Exists(Path.Combine(Root, "one.txt")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(Root, "two.txt")));
        Assert.Equal("concurrent target", File.ReadAllText(Path.Combine(Target, "two.txt")));
        await Error("INVALID_REQUEST", () => folders.ListAsync("dock", "p", "main", listing.FolderId));
        await Error("INVALID_REQUEST", () => folders.ListAsync("dock", "target", "main", target.FolderId));
    }

    [Fact]
    public async Task CopyPathWorksForAFileCurrentlyOpenForEditing()
    {
        var listing = await List(); var path = Path.Combine(Root, "one.txt");
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        Assert.Equal(path, await _mutations.GetPathAsync("dock", "p", "main", Id(listing, "one.txt")));
    }

    [Fact]
    public async Task SameBindingMoveUsesARealChildDirectoryToken()
    {
        var listing = await List();
        var result = await _mutations.MoveAsync("dock", "p", "main", [Id(listing, "one.txt")], "p", "main", Id(listing, "child"));
        Assert.True(result.Completed); Assert.Equal(1, result.ChangedCount);
        Assert.False(File.Exists(Path.Combine(Root, "one.txt")));
        Assert.Equal("one", File.ReadAllText(Path.Combine(Root, "child", "one.txt")));
    }

    [Fact]
    public async Task CrossVolumeMoveIsRejectedWithoutCopyingOrDeleting()
    {
        // This machine's test binaries and TEMP are on distinct volumes. Keep any second fixture inside test output.
        if (string.Equals(Path.GetPathRoot(_base), Path.GetPathRoot(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase)) return;
        var other = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "mutation-volume-fixture-" + Guid.NewGuid().ToString("N")));
        Assert.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), other);
        Directory.CreateDirectory(other);
        try
        {
            var state = _store.Current; state.Projects.Single(p => p.Id == "target").Items[0].Path = other;
            Assert.Equal(SaveOutcome.Saved, _store.Save(state, state.Revision).Outcome);
            var listing = await List(); var target = await List("target");
            await Error("INVALID_REQUEST", () => _mutations.MoveAsync("dock", "p", "main", [Id(listing, "one.txt")], "target", "main", target.FolderId));
            Assert.Equal("one", File.ReadAllText(Path.Combine(Root, "one.txt")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(other));
        }
        finally { Directory.Delete(other, false); }
    }

    [Fact]
    public async Task SlowPreflightRetainsTwoSlotsAndNeverWritesAfterTimeout()
    {
        var files = new InterceptFiles(); var folders = new FolderService(_store, _shell, files);
        var listing = await folders.ListAsync("dock", "p", "main");
        using var entered = new CountdownEvent(2); using var release = new ManualResetEventSlim();
        var reads = 0;
        files.AfterAttributes = path =>
        {
            if (path != Root) return;
            if (Interlocked.Increment(ref reads) <= 2) entered.Signal();
            release.Wait(TimeSpan.FromSeconds(5));
        };
        var service = new FolderMutationService(folders, TimeSpan.FromMilliseconds(100));
        var first = service.CreateFolderAsync("dock", "p", "main", listing.FolderId, "late1");
        var second = service.CreateFolderAsync("dock", "p", "main", listing.FolderId, "late2");
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            await Error("BUSY", () => first); await Error("BUSY", () => second);
            await Error("BUSY", () => service.CreateFolderAsync("dock", "p", "main", listing.FolderId, "late3"));
            Assert.Equal(2, reads);
        }
        finally
        {
            files.AfterAttributes = null; release.Set();
            // Drain the actual workers before the fixture can be removed or the next test acquires their slots.
            for (var i = 0; i < 200; i++)
            {
                try { await service.GetPathAsync("dock", "p", "main", listing.FolderId); break; }
                catch (FolderOperationException ex) when (ex.Code == "BUSY" && i < 199) { await Task.Delay(10); }
            }
        }
        Assert.False(Directory.Exists(Path.Combine(Root, "late1")));
        Assert.False(Directory.Exists(Path.Combine(Root, "late2")));
        Assert.False(Directory.Exists(Path.Combine(Root, "late3")));
    }

    [Fact]
    public async Task FinalAuthorityCheckRejectsConfigChangeBeforeWriting()
    {
        var files = new InterceptFiles(); var folders = new FolderService(_store, _shell, files);
        var listing = await folders.ListAsync("dock", "p", "main");
        var reads = 0;
        files.AfterAttributes = path =>
        {
            if (path != Root || ++reads != 3) return;
            var state = _store.Current; state.Projects[0].Items[0].Path = Target;
            Assert.Equal(SaveOutcome.Saved, _store.Save(state, state.Revision).Outcome);
        };
        await Error("INVALID_REQUEST", () => new FolderMutationService(folders).CreateFolderAsync("dock", "p", "main", listing.FolderId, "late"));
        Assert.False(Directory.Exists(Path.Combine(Root, "late"))); Assert.False(Directory.Exists(Path.Combine(Target, "late")));
    }

    private sealed class InterceptFiles : IFolderFileSystem
    {
        public Action<string>? AfterAttributes;
        public FileAttributes GetAttributes(string path) { var attributes = File.GetAttributes(path); AfterAttributes?.Invoke(path); return attributes; }
        public IEnumerable<FolderDiskEntry> Enumerate(string path) => new RealFolderFileSystem().Enumerate(path);
    }
    private static void CreateJunction(string directory, string target)
    {
        var substitute = @"\??\" + target;
        var names = Encoding.Unicode.GetBytes(substitute + '\0' + target + '\0');
        var data = new byte[16 + names.Length];
        BitConverter.GetBytes(0xA0000003u).CopyTo(data, 0);
        BitConverter.GetBytes(checked((ushort)(data.Length - 8))).CopyTo(data, 4);
        BitConverter.GetBytes(checked((ushort)(substitute.Length * 2))).CopyTo(data, 10);
        BitConverter.GetBytes(checked((ushort)((substitute.Length + 1) * 2))).CopyTo(data, 12);
        BitConverter.GetBytes(checked((ushort)(target.Length * 2))).CopyTo(data, 14);
        names.CopyTo(data, 16);
        using var handle = CreateFile(directory, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!DeviceIoControl(handle, 0x900A4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
