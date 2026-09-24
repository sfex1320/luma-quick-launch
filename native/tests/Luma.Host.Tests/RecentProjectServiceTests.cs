using System.IO;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

public sealed class RecentProjectServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "luma-recent-tests-" + Guid.NewGuid().ToString("N"));
    private readonly StateStore _store;
    private readonly Source _source = new();
    private readonly List<string> _opened = new();
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly RecentProjectService _service;
    public RecentProjectServiceTests()
    {
        _store = new(_directory); _store.Load();
        SaveApp(@"C:\Apps\Photoshop.exe");
        _service = new(_store, _source, (path, cancel) => { cancel.ThrowIfCancellationRequested(); _opened.Add(path); return null; }, () => _now);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private void SaveApp(string path)
    {
        var state = TestStates.OneProject("p", path);
        state.Projects[0].Items[0].Kind = "app";
        _store.Save(state, _store.Current.Revision);
        _source.Executable = path;
    }
    private void Add(string path, int age = 0)
    { _source.Rows.Add(new(path, _now.AddMinutes(-age))); _source.Files.Add(path); }
    private Task<RecentProjectListing> Get(int limit = 6) => _service.GetAsync("dock", "p", "main", limit);
    private static async Task Error(string code, Func<Task> work)
    { Assert.Equal(code, (await Assert.ThrowsAsync<FolderOperationException>(work)).Code); }

    [Fact]
    public async Task CapabilitiesResolveSavedSoftwareWithoutReadingHistory()
    {
        Assert.True(await _service.SupportsRecentAsync("dock", "p", "main"));
        SaveApp(@"C:\Apps\Kimi Code.lnk");
        _source.Executable = @"C:\Apps\Kimi.exe";
        Assert.False(await _service.SupportsRecentAsync("dock", "p", "main"));
        Assert.Equal(0, _source.Reads);
        Assert.Empty(_opened);
    }

    [Fact]
    public async Task PhotoshopOnlyGetsExistingDedicatedFormatsSortedAndDeduplicated()
    {
        Add(@"C:\Art\old.psd", 10); Add(@"C:\Art\recent.psb", 1); Add(@"C:\Art\RECENT.psb", 2);
        Add(@"C:\Art\other.ai"); Add(@"C:\Art\photo.png"); Add(@"C:\Art\folder");
        _source.Rows.Add(new(@"C:\Art\deleted.psd", _now));
        var listing = await Get();
        Assert.Equal(new[] { @"C:\Art\recent.psb", @"C:\Art\old.psd" }, listing.Entries.Select(e => e.Path));
        Assert.All(listing.Entries, e => { Assert.Equal("file", e.Kind); Assert.NotEqual(e.Path, e.Id); });
        Assert.Contains("Windows", listing.Note);
        Assert.Empty(_opened);
        Assert.Equal(@"C:\Apps\Photoshop.exe", _source.LastReadExecutable);
        Assert.True(await _service.OpenAsync("dock", "p", "main", listing.Entries[0].Id));
        Assert.Equal(@"C:\Art\recent.psb", Assert.Single(_opened));
    }
    [Theory]
    [InlineData("Illustrator.exe", ".ai")]
    [InlineData("Illustrator.exe", ".eps")]
    [InlineData("InDesign.exe", ".indd")]
    public async Task DedicatedApplicationIdentitySelectsItsDocumentExtension(string exe, string extension)
    {
        SaveApp(@"C:\Apps\" + exe); Add(@"C:\Art\project" + extension); Add(@"C:\Art\wrong.psd");
        Assert.Equal("project" + extension, Assert.Single((await Get()).Entries).Name);
    }
    [Fact]
    public async Task UnknownOrRenamedDisplayLabelCannotClaimAllRecentDocuments()
    {
        SaveApp(@"C:\Apps\Unknown.exe"); Add(@"C:\Art\project.psd");
        var state = _store.Current; state.Projects[0].Items[0].Name = "Photoshop"; _store.Save(state, state.Revision);
        Assert.Empty((await Get()).Entries); Assert.Equal(0, _source.Reads);
    }
    [Theory]
    [InlineData("client")]
    [InlineData("project")]
    [InlineData("item")]
    [InlineData("path")]
    [InlineData("exe")]
    [InlineData("deleted")]
    [InlineData("expired")]
    [InlineData("detach")]
    public async Task StaleOrForeignCapabilityCannotOpen(string change)
    {
        Add(@"C:\Art\project.psd"); var entry = Assert.Single((await Get()).Entries);
        if (change == "exe") _source.Executable = @"C:\Other\Photoshop.exe";
        if (change == "path") SaveApp(@"C:\Other\Photoshop.exe");
        if (change == "deleted") _source.Files.Clear();
        if (change == "expired") _now = _now.AddMinutes(3);
        if (change == "detach") _service.Detach("dock");
        await Assert.ThrowsAsync<FolderOperationException>(() => _service.OpenAsync(change == "client" ? "settings" : "dock",
            change == "project" ? "other" : "p", change == "item" ? "other" : "main", entry.Id));
        Assert.Empty(_opened);
    }
    [Fact]
    public async Task ScanAndResultAreBoundedAndRawPathCannotBeOpened()
    {
        for (var i = 0; i < 300; i++) Add($@"C:\Art\project{i}.psd", i);
        Assert.Equal(10, (await Get(10)).Entries.Count);
        Assert.Equal(256, _source.Yielded);
        await Error("INVALID_REQUEST", () => _service.OpenAsync("dock", "p", "main", @"C:\Art\project0.psd"));
        await Error("INVALID_REQUEST", () => Get(11));
        Assert.Empty(_opened);
    }
    [Theory]
    [InlineData(@"\\?\C:\Art\project.psd")]
    [InlineData(@"C:\Art\project.psd:payload.psd")]
    [InlineData(@"C:\Art\..\elsewhere.psd")]
    [InlineData("https://example.com/project.psd")]
    public async Task InvalidMetadataPathsNeverBecomeCapabilities(string path)
    { Add(path); Assert.Empty((await Get()).Entries); }
    [Fact]
    public async Task TimeoutDuringDetectionCannotIssueLateCapability()
    {
        Add(@"C:\Art\project.psd"); var entry = Assert.Single((await Get()).Entries);
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var ended = new ManualResetEventSlim();
        _source.BeforeResolve = () => { started.Set(); release.Wait(); ended.Set(); };
        var service = new RecentProjectService(_store, _source, (path, cancel) => { _opened.Add(path); return null; }, timeout: TimeSpan.FromMilliseconds(80));
        try
        {
            var get = service.GetAsync("dock", "p", "main", 6);
            Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
            await Error("BUSY", () => get);
        }
        finally { release.Set(); Assert.True(ended.Wait(TimeSpan.FromSeconds(2))); }
        _source.BeforeResolve = null;
        _service.Detach("dock");
        await Error("INVALID_REQUEST", () => _service.OpenAsync("dock", "p", "main", entry.Id));
        Assert.Empty(_opened);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutOrDetachDuringOpenCannotLaunchAfterSlowResolution(bool detach)
    {
        Add(@"C:\Art\project.psd");
        var service = new RecentProjectService(_store, _source, (path, cancel) => { _opened.Add(path); return null; },
            timeout: TimeSpan.FromMilliseconds(120));
        var entry = Assert.Single((await service.GetAsync("dock", "p", "main", 6)).Entries);
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var ended = new ManualResetEventSlim();
        _source.BeforeResolve = () => { started.Set(); release.Wait(); ended.Set(); };
        var open = service.OpenAsync("dock", "p", "main", entry.Id);
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
            if (detach) { service.Detach("dock"); release.Set(); await Error("CANCELLED", () => open); }
            else await Error("BUSY", () => open);
        }
        finally { release.Set(); Assert.True(ended.Wait(TimeSpan.FromSeconds(2))); }
        // Allow the same two STA workers to finish their cancellation checks before asserting side effects.
        _source.BeforeResolve = null;
        await service.GetAsync("other", "p", "main", 6);
        Assert.Empty(_opened);
    }
    internal sealed class Source : IRecentProjectSource
    {
        public string? Executable;
        public List<RecentProjectDocument> Rows = new();
        public HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase);
        public int Reads, Yielded;
        public string? LastReadExecutable;
        public Action? BeforeResolve;
        public string? ResolveExecutable(string savedPath, CancellationToken cancellation) { BeforeResolve?.Invoke(); return Executable; }
        public IEnumerable<RecentProjectDocument> ReadRecent(string executable, CancellationToken cancellation)
        { Reads++; LastReadExecutable = executable; foreach (var row in Rows) { Yielded++; yield return row; } }
        public bool IsRegularFile(string path) => Files.Contains(path);
    }
}
