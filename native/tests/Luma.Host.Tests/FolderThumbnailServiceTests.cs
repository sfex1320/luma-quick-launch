using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

[Collection("Folder IO")]
public class FolderThumbnailServiceTests : IDisposable
{
    private const string Png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aWZkAAAAASUVORK5CYII=";
    private readonly string _base = Path.Combine(Path.GetTempPath(), "luma-thumbnails", Guid.NewGuid().ToString("N"));
    private readonly StateStore _store;
    private readonly FakeShell _shell = new();
    private readonly FolderService _folders;
    private readonly AttributeFiles _files = new();
    private string Root => Path.Combine(_base, "root");
    public FolderThumbnailServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(Root, "child"));
        File.WriteAllText(Path.Combine(Root, "photo.png"), "fixture");
        _store = new(Path.Combine(_base, "config")); _store.Load();
        Assert.Equal(SaveOutcome.Saved, _store.Save(TestStates.OneProject("p", Root), 0).Outcome);
        _folders = new(_store, _shell, _files);
    }
    public void Dispose() { try { Directory.Delete(_base, true); } catch { } }
    private async Task<string> Entry() => (await _folders.ListAsync("dock", "p", "main")).Entries.Single(e => e.Name == "photo.png").Id;
    private static async Task Error(string code, Func<Task> action) => Assert.Equal(code, (await Assert.ThrowsAsync<FolderOperationException>(action)).Code);

    [Fact]
    public async Task FileNamesContainingShellWordsRemainLiteralPreviewPaths()
    {
        var file = Path.Combine(Root, "rock && roll taskkill.png");
        File.WriteAllText(file, "fixture");
        var id = (await _folders.ListAsync("dock", "p", "main")).Entries.Single(e => e.Name == Path.GetFileName(file)).Id;
        var service = new FolderThumbnailService(_folders, (path, _) => { Assert.Equal(file, path); return Png; });
        Assert.Equal(Png, await service.GetAsync("dock", "p", "main", id, 96));
    }

    [Fact]
    public async Task CoalescesStaticStaReadsAndRevalidatesCachedFileMetadata()
    {
        var id = await Entry();
        var reads = 0;
        var service = new FolderThumbnailService(_folders, (path, size) =>
        {
            Assert.Equal(Path.Combine(Root, "photo.png"), path);
            Assert.Equal(96, size);
            Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            Interlocked.Increment(ref reads); return Png;
        });
        Assert.All(await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => service.GetAsync("dock", "p", "main", id, 96))), value => Assert.Equal(Png, value));
        Assert.Equal(Png, await service.GetAsync("dock", "p", "main", id, 96));
        Assert.Equal(1, reads);
        File.AppendAllText(Path.Combine(Root, "photo.png"), "changed");
        Assert.Equal(Png, await service.GetAsync("dock", "p", "main", id, 96));
        Assert.Equal(2, reads);
        File.Delete(Path.Combine(Root, "photo.png"));
        await Error("PATH_NOT_FOUND", () => service.GetAsync("dock", "p", "main", id, 96));
        Assert.Empty(_shell.Launched); Assert.Equal(1, _store.Current.Revision);
    }

    [Fact]
    public async Task CachedReadsRejectForeignClientRootChangeDetachAndDirectories()
    {
        var listing = await _folders.ListAsync("dock", "p", "main");
        var id = listing.Entries.Single(e => e.Name == "photo.png").Id;
        var service = new FolderThumbnailService(_folders, (_, _) => Png);
        Assert.Equal(Png, await service.GetAsync("dock", "p", "main", id));
        await Error("INVALID_REQUEST", () => service.GetAsync("other", "p", "main", id));
        await Error("INVALID_REQUEST", () => service.GetAsync("dock", "p", "main", Root));
        await Error("INVALID_REQUEST", () => service.GetAsync("dock", "p", "main", listing.FolderId));
        _folders.Detach("dock");
        await Error("INVALID_REQUEST", () => service.GetAsync("dock", "p", "main", id));
        id = await Entry();
        var state = _store.Current;
        state.Projects[0].Items[0].Path = Path.Combine(Root, "child");
        Assert.Equal(SaveOutcome.Saved, _store.Save(state, state.Revision).Outcome);
        await Error("INVALID_REQUEST", () => service.GetAsync("dock", "p", "main", id));
    }

    [Fact]
    public async Task TokensRemainBoundToSavedProjectAndItemAndExpireEvenWhenCached()
    {
        var now = DateTimeOffset.UtcNow;
        var folders = new FolderService(_store, _shell, clock: () => now);
        var id = (await folders.ListAsync("dock", "p", "main")).Entries.Single(e => e.Name == "photo.png").Id;
        var state = _store.Current;
        state.Projects.Add(TestStates.OneProject("other", Root).Projects[0]);
        state.Projects[0].Items.Add(new LaunchItem { Id = "other", Name = "other", Kind = "folder", Path = Root });
        Assert.Equal(SaveOutcome.Saved, _store.Save(state, state.Revision).Outcome);
        var service = new FolderThumbnailService(folders, (_, _) => Png);
        Assert.Equal(Png, await service.GetAsync("dock", "p", "main", id));
        await Error("INVALID_REQUEST", () => service.GetAsync("dock", "other", "main", id));
        await Error("INVALID_REQUEST", () => service.GetAsync("dock", "p", "other", id));
        now = now.AddMinutes(5);
        await Error("INVALID_REQUEST", () => service.GetAsync("dock", "p", "main", id));
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("traversal")]
    [InlineData("device")]
    public async Task MalformedEnumeratorEntryCannotEscapeSavedRoot(string kind)
    {
        var path = kind switch
        {
            "outside" => Path.Combine(_base, "outside.png"),
            "traversal" => Path.Combine(Root, "..", "outside.png"),
            _ => @"\\?\C:\outside.png",
        };
        _files.Entries = [new(path, "outside.png", false)];
        var id = (await _folders.ListAsync("dock", "p", "main")).Entries.Single().Id;
        var calls = 0;
        var service = new FolderThumbnailService(_folders, (_, _) => { calls++; return Png; });
        await Error("INVALID_REQUEST", () => service.GetAsync("dock", "p", "main", id));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("link.lnk")]
    [InlineData("link.url")]
    [InlineData("link.website")]
    public async Task ShellLinksAreRejectedBeforeExtraction(string name)
    {
        File.WriteAllText(Path.Combine(Root, name), "fixture");
        var id = (await _folders.ListAsync("dock", "p", "main")).Entries.Single(e => e.Name == name).Id;
        var calls = 0;
        var service = new FolderThumbnailService(_folders, (_, _) => { calls++; return Png; });
        await Error("ACCESS_DENIED", () => service.GetAsync("dock", "p", "main", id));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(FileAttributes.ReparsePoint)]
    [InlineData(FileAttributes.Device)]
    public async Task CachedReadRejectsDeviceAndReparseSubstitution(FileAttributes blocked)
    {
        var id = await Entry();
        var calls = 0;
        var service = new FolderThumbnailService(_folders, (_, _) => { calls++; return Png; });
        Assert.Equal(Png, await service.GetAsync("dock", "p", "main", id));
        _files.Attributes = blocked;
        await Error("ACCESS_DENIED", () => service.GetAsync("dock", "p", "main", id));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task UnsupportedHandlerReturnsNullAndMalformedOrOversizedPngCannotEscape()
    {
        var id = await Entry();
        foreach (var result in new[] { null, "data:image/jpeg;base64,AA==", Png[..60], MakePng(129), "data:image/png;base64," + new string('A', 350001) })
        {
            var service = new FolderThumbnailService(_folders, (_, _) => result);
            Assert.Null(await service.GetAsync("dock", "p", "main", id, 128));
        }
    }

    [Fact]
    public async Task CacheBoundsEntryCountAndTotalCharacters()
    {
        for (var i = 0; i < 140; i++) File.WriteAllText(Path.Combine(Root, $"file{i}.png"), "fixture");
        var entries = (await _folders.ListAsync("dock", "p", "main")).Entries.Where(e => e.Kind != "folder").ToArray();
        var small = new FolderThumbnailService(_folders, (_, _) => Png);
        foreach (var entry in entries) Assert.Equal(Png, await small.GetAsync("dock", "p", "main", entry.Id));
        Assert.Equal(128, small.CacheUsage.Entries);
        var image = MakePng(128);
        var large = new FolderThumbnailService(_folders, (_, _) => image);
        foreach (var entry in entries) Assert.Equal(image, await large.GetAsync("dock", "p", "main", entry.Id, 128));
        Assert.InRange(large.CacheUsage.Characters, 1, 6_000_000);
        Assert.InRange(large.CacheUsage.Entries, 1, 127);
        Assert.Equal(0, large.CacheUsage.Pending);
    }

    [Fact]
    public async Task SlowValidationStaysOffCallerAndDoesNotHoldServiceLock()
    {
        var id = await Entry();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        _files.BeforeAttributes = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); };
        var calls = 0;
        var service = new FolderThumbnailService(_folders, (_, _) => { calls++; return Png; }, TimeSpan.FromMilliseconds(100));
        try
        {
            var pending = service.GetAsync("dock", "p", "main", id);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.Equal(1, service.CacheUsage.Pending);
            Assert.Null(await pending);
        }
        finally { _files.BeforeAttributes = null; release.Set(); await Drain(service); }
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task TimeoutKeepsTwoWorkersAndExpiredQueueDoesNotExtractLater()
    {
        for (var i = 0; i < 140; i++) File.WriteAllText(Path.Combine(Root, $"file{i}.png"), "fixture");
        var entries = (await _folders.ListAsync("dock", "p", "main")).Entries.Where(e => e.Kind != "folder").ToArray();
        using var entered = new CountdownEvent(2); using var release = new ManualResetEventSlim();
        var reads = 0;
        var service = new FolderThumbnailService(_folders, (_, _) =>
        { if (Interlocked.Increment(ref reads) <= 2) entered.Signal(); release.Wait(TimeSpan.FromSeconds(5)); return Png; }, TimeSpan.FromMilliseconds(150));
        try
        {
            var pending = entries.Select(e => service.GetAsync("dock", "p", "main", e.Id)).ToArray();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.All(await Task.WhenAll(pending), value => Assert.Null(value));
            Assert.InRange(service.CacheUsage.Pending, 2, 128);
            Assert.Equal(2, reads);
            Assert.Null(await service.GetAsync("dock", "p", "main", entries[0].Id));
            Assert.Equal(2, reads);
        }
        finally { release.Set(); await Drain(service); }
        Assert.Equal(2, reads); Assert.Equal(0, service.CacheUsage.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowExtractionDiscardsRootChangesAndDetach(bool detach)
    {
        var id = await Entry();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var service = new FolderThumbnailService(_folders, (_, _) => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); return Png; });
        var work = service.GetAsync("dock", "p", "main", id);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            if (detach) _folders.Detach("dock");
            else
            {
                var state = _store.Current; state.Projects[0].Items[0].Path = Path.Combine(Root, "child");
                Assert.Equal(SaveOutcome.Saved, _store.Save(state, state.Revision).Outcome);
            }
        }
        finally { release.Set(); }
        await Error("INVALID_REQUEST", () => work);
        Assert.Equal(0, service.CacheUsage.Entries);
    }

    private static async Task Drain(FolderThumbnailService service)
    {
        for (var i = 0; i < 200 && service.CacheUsage.Pending > 0; i++) await Task.Delay(10);
        Assert.Equal(0, service.CacheUsage.Pending);
    }
    private static string MakePng(int size)
    {
        var pixels = new byte[size * size * 4]; new Random(7).NextBytes(pixels);
        var source = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream(); encoder.Save(stream);
        return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
    }
    private sealed class AttributeFiles : IFolderFileSystem
    {
        public FileAttributes Attributes;
        public Action? BeforeAttributes;
        public IEnumerable<FolderDiskEntry>? Entries;
        private readonly RealFolderFileSystem _real = new();
        public FileAttributes GetAttributes(string path) { BeforeAttributes?.Invoke(); return _real.GetAttributes(path) | Attributes; }
        public IEnumerable<FolderDiskEntry> Enumerate(string path) => Entries ?? _real.Enumerate(path);
    }
}
