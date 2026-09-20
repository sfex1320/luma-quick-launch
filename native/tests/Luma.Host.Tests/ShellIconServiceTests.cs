using System.IO;
using System.Windows.Media.Imaging;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Xunit;
using Xunit.Abstractions;

namespace Luma.Host.Tests;

[Collection("Shell icon IO")]
public class ShellIconServiceTests : IDisposable
{
    private const string Png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aWZkAAAAASUVORK5CYII=";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "luma-shell-icons", Guid.NewGuid().ToString("N"));
    private readonly StateStore _store;
    private readonly ITestOutputHelper _output;
    public ShellIconServiceTests(ITestOutputHelper output) { _output = output; _store = new(_dir); _store.Load(); }
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
    private void Save(string path)
    {
        var state = TestStates.OneProject("p", path); state.Projects[0].Items[0].Kind = "app";
        state.Revision = _store.Current.Revision;
        Assert.Equal(SaveOutcome.Saved, _store.Save(state, state.Revision).Outcome);
    }

    [Fact]
    public async Task CoalescesDuplicateReadsAndInvalidatesAfterSavedPathChanges()
    {
        Save(@"C:\one.exe");
        var paths = new List<string>();
        var service = new ShellIconService(_store, (path, _) => { paths.Add(path); Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState()); return Png; });
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => service.GetAsync("p", "main")));
        Assert.All(results, value => Assert.Equal(Png, value));
        Assert.Single(paths);
        Assert.Equal(Png, await service.GetAsync("p", "main"));
        Assert.Single(paths);
        Save(@"C:\two.exe");
        Assert.Equal(Png, await service.GetAsync("p", "main"));
        Assert.Equal(new[] { @"C:\one.exe", @"C:\two.exe" }, paths);
        Assert.Equal(2, _store.Current.Revision);
    }

    [Fact]
    public async Task StaleCompletionIsDiscardedAndTimeoutDoesNotStartDuplicateWork()
    {
        Save(@"C:\old.exe");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var reads = 0;
        var service = new ShellIconService(_store, (_, _) => { Interlocked.Increment(ref reads); entered.Set(); release.Wait(TimeSpan.FromSeconds(3)); return Png; }, TimeSpan.FromMilliseconds(80));
        var first = service.GetAsync("p", "main");
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        Assert.Null(await first);
        Assert.Null(await service.GetAsync("p", "main"));
        Assert.Equal(1, reads);
        Save(@"C:\new.exe"); release.Set();
        Assert.Equal(Png, await service.GetAsync("p", "main"));
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task SavedPathChangeWhileShellIsReadingDiscardsOldCompletion()
    {
        Save(@"C:\old.exe");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var service = new ShellIconService(_store, (_, _) => { entered.Set(); release.Wait(TimeSpan.FromSeconds(3)); return Png; });
        var first = service.GetAsync("p", "main");
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        Save(@"C:\new.exe"); release.Set();
        Assert.Null(await first);
        Assert.Equal(Png, await service.GetAsync("p", "main"));
    }

    [Fact]
    public async Task RealWindowsExecutableReturnsDecodablePngAtRequestedSize()
    {
        Save(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        var data = await new ShellIconService(_store).GetAsync("p", "main", 96);
        ValidateImage(data, "Windows explorer.exe", 96);
    }

    [Fact]
    public async Task RealPhotoshopShortcutReturnsDecodablePngWhenPresent()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Adobe Photoshop 2026.lnk");
        if (!File.Exists(path)) { _output.WriteLine("Photoshop shortcut unavailable on this machine; portable test did not inspect it."); return; }
        var before = File.ReadAllBytes(path);
        Save(path);
        ValidateImage(await new ShellIconService(_store).GetAsync("p", "main", 96), "Adobe Photoshop 2026.lnk", 96);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task RealMomoShortcutIsReadOnlyAndReturnsDecodablePngWhenPresent()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MOMO 智能画布.lnk");
        if (!File.Exists(path)) { _output.WriteLine("MOMO desktop shortcut unavailable on this machine; portable test did not inspect it."); return; }
        var before = File.ReadAllBytes(path);
        Save(path);
        var data = await new ShellIconService(_store).GetAsync("p", "main", 96);
        ValidateImage(data, "MOMO 智能画布.lnk", 96);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(1, _store.Current.Revision);
    }

    private void ValidateImage(string? data, string name, int maximum)
    {
        Assert.NotNull(data); Assert.StartsWith("data:image/png;base64,", data); Assert.True(data.Length <= 350000);
        using var stream = new MemoryStream(Convert.FromBase64String(data[22..]));
        var image = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.Single();
        Assert.InRange(image.PixelWidth, 1, maximum); Assert.InRange(image.PixelHeight, 1, maximum);
        _output.WriteLine($"Real Shell icon {name}: {image.PixelWidth}x{image.PixelHeight}, {data.Length} data URL characters; no launch.");
    }
}
