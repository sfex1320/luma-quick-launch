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
public class FolderTransferTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "luma-transfer-tests", Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_base, "source");
    private string Target => Path.Combine(_base, "target");
    private readonly StateStore _store;
    private readonly FakeShell _shell = new();
    private readonly FolderService _folders;
    private readonly FolderMutationService _service;

    public FolderTransferTests()
    {
        Directory.CreateDirectory(Source); Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Source, "one.txt"), "first source");
        File.WriteAllText(Path.Combine(Source, "two.txt"), "second source");
        _store = new(Path.Combine(_base, "config")); _store.Load();
        Assert.Equal(SaveOutcome.Saved, _store.Save(TestStates.OneProject("target", Target), 0).Outcome);
        _folders = new(_store, _shell); _service = new(_folders);
    }

    public void Dispose() { try { Directory.Delete(_base, true); } catch { } }

    private async Task<FolderMutationResult> Transfer(string operation, params string[] sources)
    {
        var target = await _folders.ListAsync("dock", "target", "main");
        return await _service.TransferAsync("dock", "target", "main", target.FolderId, sources, operation);
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("move")]
    public async Task CopyAndMoveTransferRealBytesWithoutLaunching(string operation)
    {
        var source = Path.Combine(Source, "one.txt");
        var result = await Transfer(operation, source);
        Assert.True(result.Completed); Assert.Equal(1, result.ChangedCount);
        Assert.Equal("first source", File.ReadAllText(Path.Combine(Target, "one.txt")));
        Assert.Equal(operation == "copy", File.Exists(source));
        Assert.Empty(_shell.Launched); Assert.Equal(1, _store.Current.Revision);
    }

    [Fact]
    public async Task CopyRecursesAndRetainsSourceBytes()
    {
        var folder = Path.Combine(Source, "tree"); Directory.CreateDirectory(Path.Combine(folder, "sub", "empty"));
        var contents = Enumerable.Range(0, 300000).Select(i => (byte)(i % 251)).ToArray();
        File.WriteAllBytes(Path.Combine(folder, "sub", "内容.bin"), contents);
        var result = await Transfer("copy", folder);
        Assert.True(result.Completed); Assert.Equal(1, result.ChangedCount);
        Assert.Equal(contents, File.ReadAllBytes(Path.Combine(Target, "tree", "sub", "内容.bin")));
        Assert.Equal(contents, File.ReadAllBytes(Path.Combine(folder, "sub", "内容.bin")));
        Assert.True(Directory.Exists(Path.Combine(Target, "tree", "sub", "empty")));
    }

    [Fact]
    public async Task LinkProducesRealWindowsShortcutWithCorrectTargetWithoutLaunching()
    {
        var source = Path.Combine(Source, "one.txt");
        var result = await Transfer("link", source);
        Assert.True(result.Completed); Assert.Equal(1, result.ChangedCount);
        var link = Path.Combine(Target, "one.txt.lnk");
        Assert.Equal(0x4c, BitConverter.ToInt32(File.ReadAllBytes(link), 0));
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)!;
        object? shortcut = null;
        try
        {
            shortcut = shell.CreateShortcut(link);
            Assert.Equal(source, (string)((dynamic)shortcut).TargetPath, ignoreCase: true);
            Assert.Equal("", (string)((dynamic)shortcut).Arguments);
        }
        finally { if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
        Assert.Equal("first source", File.ReadAllText(source)); Assert.Empty(_shell.Launched);
    }

    [Theory]
    [InlineData("copy")][InlineData("move")][InlineData("link")]
    public async Task EntireBatchPreflightRejectsCollisionWithoutChangingAnySource(string operation)
    {
        var collision = Path.Combine(Target, "two.txt" + (operation == "link" ? ".lnk" : ""));
        File.WriteAllText(collision, "keep me");
        await Error("INVALID_REQUEST", () => Transfer(operation, Path.Combine(Source, "one.txt"), Path.Combine(Source, "two.txt")));
        Assert.Equal("keep me", File.ReadAllText(collision)); Assert.Single(Directory.GetFileSystemEntries(Target));
        Assert.Equal("first source", File.ReadAllText(Path.Combine(Source, "one.txt")));
        Assert.Equal("second source", File.ReadAllText(Path.Combine(Source, "two.txt")));
    }

    [Theory]
    [InlineData("copy")][InlineData("move")][InlineData("link")]
    public async Task CollisionAfterPreflightReturnsPartialResultAndInvalidatesTokens(string operation)
    {
        var target = await _folders.ListAsync("dock", "target", "main");
        var calls = 0;
        var collision = Path.Combine(Target, "two.txt" + (operation == "link" ? ".lnk" : ""));
        var result = await _service.TransferAsync("dock", "target", "main", target.FolderId,
            [Path.Combine(Source, "one.txt"), Path.Combine(Source, "two.txt")], operation,
            () => { if (++calls == 2) File.WriteAllText(collision, "concurrent data"); });
        Assert.False(result.Completed); Assert.Equal(1, result.ChangedCount); Assert.Equal("INVALID_REQUEST", result.ErrorCode);
        Assert.Equal(2, calls); Assert.Equal("concurrent data", File.ReadAllText(collision));
        Assert.Equal("second source", File.ReadAllText(Path.Combine(Source, "two.txt")));
        await Error("INVALID_REQUEST", () => _folders.ListAsync("dock", "target", "main", target.FolderId));
    }

    [Fact]
    public async Task SourcesRevalidatedBeforeCommitAndRevocationProducesNoChange()
    {
        var target = await _folders.ListAsync("dock", "target", "main"); var calls = 0;
        await Error("INVALID_REQUEST", () => _service.TransferAsync("dock", "target", "main", target.FolderId,
            [Path.Combine(Source, "one.txt")], "copy", () => { if (++calls == 2) throw new FolderOperationException("INVALID_REQUEST", "revoked"); }));
        Assert.Equal(2, calls); Assert.Empty(Directory.GetFileSystemEntries(Target));
    }

    [Fact]
    public async Task TargetExpiredAndForeignClientTokensCannotWrite()
    {
        var now = DateTimeOffset.UtcNow;
        var folders = new FolderService(_store, _shell, clock: () => now);
        var target = await folders.ListAsync("dock", "target", "main");
        var service = new FolderMutationService(folders);
        await Error("INVALID_REQUEST", () => service.TransferAsync("settings", "target", "main", target.FolderId, [Path.Combine(Source, "one.txt")], "copy"));
        now = now.AddHours(1);
        await Error("INVALID_REQUEST", () => service.TransferAsync("dock", "target", "main", target.FolderId, [Path.Combine(Source, "one.txt")], "copy"));
        Assert.Empty(Directory.GetFileSystemEntries(Target));
    }

    [Theory]
    [InlineData("copy")][InlineData("move")][InlineData("link")]
    public async Task SelfContainmentAndOverlappingSourcesAreRejected(string operation)
    {
        await Error("INVALID_REQUEST", () => Transfer(operation, _base));
        await Error("INVALID_REQUEST", () => Transfer(operation, Source, Path.Combine(Source, "one.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(Target));
    }

    [Theory]
    [InlineData("copy")][InlineData("move")][InlineData("link")]
    public async Task ReparseSourceAndAncestorAreRejected(string operation)
    {
        var junction = Path.Combine(_base, "junction"); Directory.CreateDirectory(junction); CreateJunction(junction, Source);
        await Error("ACCESS_DENIED", () => Transfer(operation, junction));
        await Error("ACCESS_DENIED", () => Transfer(operation, Path.Combine(junction, "one.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(Target));
    }

    [Fact]
    public async Task RecursiveCopyRejectsReparseDescendantDuringPreflight()
    {
        var junction = Path.Combine(Source, "junction"); Directory.CreateDirectory(junction); CreateJunction(junction, Target);
        await Error("ACCESS_DENIED", () => Transfer("copy", Source));
        Assert.Empty(Directory.GetFileSystemEntries(Target));
    }

    [Fact]
    public async Task RecursiveEntryAndDepthLimitsFailBeforeWriting()
    {
        var many = Path.Combine(Source, "many"); Directory.CreateDirectory(many);
        for (var i = 0; i < 201; i++) File.WriteAllText(Path.Combine(many, $"item{i}.txt"), "");
        await Error("INVALID_REQUEST", () => Transfer("copy", many));
        var deep = Path.Combine(Source, "deep"); var current = deep;
        for (var i = 0; i < 18; i++) { current = Path.Combine(current, "child"); Directory.CreateDirectory(current); }
        await Error("INVALID_REQUEST", () => Transfer("copy", deep));
        Assert.Empty(Directory.GetFileSystemEntries(Target));
    }

    [Theory]
    [InlineData("relative.txt")][InlineData("C:\\temp\\..\\one.txt")][InlineData("C:\\temp\\one.txt:stream")]
    [InlineData("\\\\?\\C:\\temp\\one.txt")][InlineData("C:\\")]
    public async Task NonCanonicalAndDeviceSourcePathsAreRejected(string source)
    {
        await Error("INVALID_REQUEST", () => Transfer("copy", source));
        Assert.Empty(Directory.GetFileSystemEntries(Target));
    }

    [Fact]
    public async Task SourceWriteHandlesPreventUnstableCopies()
    {
        using var writer = new FileStream(Path.Combine(Source, "one.txt"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        await Error("BUSY", () => Transfer("copy", Path.Combine(Source, "one.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(Target));
    }

    [Fact]
    public async Task CopyNeverSilentlyDiscardsNamedStreams()
    {
        var source = Path.Combine(Source, "one.txt");
        File.WriteAllText(source + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var result = await Transfer("copy", source);
        Assert.True(result.Completed);
        Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\n", File.ReadAllText(Path.Combine(Target, "one.txt:Zone.Identifier")));
        Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\n", File.ReadAllText(source + ":Zone.Identifier"));
    }

    [Fact]
    public async Task DirectoryMovePreservesNestedFilesAndNamedStreams()
    {
        var source = Path.Combine(Source, "one.txt"); File.WriteAllText(source + ":Zone.Identifier", "ZoneId=3");
        var result = await Transfer("move", Source);
        Assert.True(result.Completed); Assert.False(Directory.Exists(Source));
        Assert.Equal("first source", File.ReadAllText(Path.Combine(Target, "source", "one.txt")));
        Assert.Equal("ZoneId=3", File.ReadAllText(Path.Combine(Target, "source", "one.txt:Zone.Identifier")));
    }

    [Fact]
    public async Task CopyPreservesDirectoryStreamsAndMultipleFileStreams()
    {
        File.WriteAllText(Source + ":目录说明", "directory metadata");
        var file = Path.Combine(Source, "one.txt");
        File.WriteAllText(file + ":Zone.Identifier", "ZoneId=3");
        File.WriteAllBytes(file + ":extra", [1, 4, 9, 16]);
        var result = await Transfer("copy", Source);
        Assert.True(result.Completed);
        Assert.Equal("directory metadata", File.ReadAllText(Path.Combine(Target, "source:目录说明")));
        Assert.Equal("ZoneId=3", File.ReadAllText(Path.Combine(Target, "source", "one.txt:Zone.Identifier")));
        Assert.Equal(new byte[] { 1, 4, 9, 16 }, File.ReadAllBytes(Path.Combine(Target, "source", "one.txt:extra")));
    }

    [Fact]
    public async Task CopyStreamLimitRejectsWholeBatchBeforeWriting()
    {
        var file = Path.Combine(Source, "two.txt");
        for (var i = 0; i < 33; i++) File.WriteAllText(file + $":extra{i}", "data");
        await Error("INVALID_REQUEST", () => Transfer("copy", Path.Combine(Source, "one.txt"), file));
        Assert.Empty(Directory.GetFileSystemEntries(Target));
        Assert.Equal("data", File.ReadAllText(file + ":extra32"));
    }

    [Fact]
    public async Task CopyLocksNamedStreamsAgainstConcurrentWriters()
    {
        var file = Path.Combine(Source, "one.txt");
        File.WriteAllText(file + ":Zone.Identifier", "ZoneId=3");
        using var writer = new FileStream(file + ":Zone.Identifier", FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        await Error("BUSY", () => Transfer("copy", file));
        Assert.Empty(Directory.GetFileSystemEntries(Target));
    }

    [Fact]
    public async Task PinnedSourceAncestorsCannotBeReplacedDuringFinalValidation()
    {
        var target = await _folders.ListAsync("dock", "target", "main"); var calls = 0; Exception? attempt = null;
        var result = await _service.TransferAsync("dock", "target", "main", target.FolderId, [Path.Combine(Source, "one.txt")], "copy", () =>
        {
            if (++calls != 2) return;
            try { Directory.Move(Source, Source + "-moved"); }
            catch (Exception ex) { attempt = ex; }
        });
        Assert.True(result.Completed); Assert.NotNull(attempt);
        Assert.True(Directory.Exists(Source)); Assert.Equal("first source", File.ReadAllText(Path.Combine(Target, "one.txt")));
    }

    [Fact]
    public async Task FinalTargetRevalidationRejectsChangedBinding()
    {
        var target = await _folders.ListAsync("dock", "target", "main"); var calls = 0;
        await Error("INVALID_REQUEST", () => _service.TransferAsync("dock", "target", "main", target.FolderId,
            [Path.Combine(Source, "one.txt")], "copy", () =>
            {
                if (++calls != 2) return;
                var state = _store.Current; state.Projects[0].Items[0].Path = Source;
                Assert.Equal(SaveOutcome.Saved, _store.Save(state, state.Revision).Outcome);
            }));
        Assert.Empty(Directory.GetFileSystemEntries(Target));
    }

    [Fact]
    public async Task CrossVolumeMoveIsRejectedWithoutCopyDelete()
    {
        if (string.Equals(Path.GetPathRoot(_base), Path.GetPathRoot(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase)) return;
        var other = Path.Combine(AppContext.BaseDirectory, "transfer-volume-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        try
        {
            var state = _store.Current; state.Projects[0].Items[0].Path = other;
            Assert.Equal(SaveOutcome.Saved, _store.Save(state, state.Revision).Outcome);
            await Error("INVALID_REQUEST", () => Transfer("move", Path.Combine(Source, "one.txt")));
            Assert.Equal("first source", File.ReadAllText(Path.Combine(Source, "one.txt"))); Assert.Empty(Directory.GetFileSystemEntries(other));
        }
        finally { Directory.Delete(other, false); }
    }

    private static async Task Error(string code, Func<Task> action) => Assert.Equal(code, (await Assert.ThrowsAsync<FolderOperationException>(action)).Code);

    private static void CreateJunction(string directory, string target)
    {
        var substitute = @"\??\" + target;
        var names = Encoding.Unicode.GetBytes(substitute + '\0' + target + '\0');
        var data = new byte[16 + names.Length];
        BitConverter.GetBytes(0xA0000003u).CopyTo(data, 0);
        BitConverter.GetBytes(checked((ushort)(data.Length - 8))).CopyTo(data, 4);
        BitConverter.GetBytes(checked((ushort)(substitute.Length * 2))).CopyTo(data, 10);
        BitConverter.GetBytes(checked((ushort)((substitute.Length + 1) * 2))).CopyTo(data, 12);
        BitConverter.GetBytes(checked((ushort)(target.Length * 2))).CopyTo(data, 14); names.CopyTo(data, 16);
        using var handle = CreateFile(directory, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!DeviceIoControl(handle, 0x900A4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
