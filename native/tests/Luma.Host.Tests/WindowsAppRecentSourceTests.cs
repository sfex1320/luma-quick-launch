using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

public sealed class WindowsAppRecentSourceTests
{
    [Fact]
    public void AFileNamedPhotoshopWithoutAdobeMetadataCannotClaimPrivateHistory()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "luma-recent-identity-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var executable = System.IO.Path.Combine(directory, "Photoshop.exe");
            System.IO.File.WriteAllText(executable, "not an Adobe executable");
            var source = new WindowsAppRecentSource();
            Assert.Null(source.ResolveExecutable(executable, CancellationToken.None));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("Photoshop.exe", "Photoshop")]
    [InlineData("Illustrator.exe", "illustrator")]
    [InlineData("InDesign.exe", "indesign")]
    public void UsesOnlyTheMatchingAdobeApplicationMru(string executable, string expectedApp)
    {
        string? requested = null;
        var source = new WindowsAppRecentSource((app, _) =>
        {
            requested = app;
            return [new RecentProjectDocument(@"C:\Art\one.psd", DateTimeOffset.UnixEpoch)];
        }, AdobeMetadata);
        Assert.Single(source.ReadRecent(@"C:\Apps\" + executable, CancellationToken.None));
        Assert.Equal(expectedApp, requested);
    }

    [Fact]
    public void UnsupportedApplicationDoesNotReadAnySharedHistory()
    {
        var reads = 0;
        var source = new WindowsAppRecentSource((_, _) => { reads++; return []; });
        Assert.Empty(source.ReadRecent(@"C:\Apps\AfterFX.exe", CancellationToken.None));
        Assert.Equal(0, reads);
    }

    [Fact]
    public void SourceEnumerationIsBoundedBeforeServiceFiltering()
    {
        var yielded = 0;
        IEnumerable<RecentProjectDocument> Rows()
        {
            for (var i = 0; i < 300; i++) { yielded++; yield return new($@"C:\Art\{i}.psd", DateTimeOffset.UnixEpoch); }
        }
        var source = new WindowsAppRecentSource((_, _) => Rows(), AdobeMetadata);
        Assert.Equal(256, source.ReadRecent(@"C:\Apps\Photoshop.exe", CancellationToken.None).Count());
        Assert.Equal(256, yielded);
    }

    [Theory]
    [InlineData("Adobe", "Adobe Photoshop 2026", "Photoshop.exe", true)]
    [InlineData("Adobe Inc.", "Adobe Photoshop CC", "PHOTOSHOP.EXE", true)]
    [InlineData("Adobe Systems Incorporated", "Adobe Photoshop", "Photoshop.exe", true)]
    [InlineData("Other Publisher", "Adobe Photoshop 2026", "Photoshop.exe", false)]
    [InlineData("Adobe", "Adobe Illustrator 2026", "Photoshop.exe", false)]
    [InlineData("Adobe", "Adobe PhotoshopFake", "Photoshop.exe", false)]
    [InlineData("Adobe", "Adobe Photoshop 2026", "Other.exe", false)]
    [InlineData(null, null, null, false)]
    public void RequiresConsistentPublisherProductAndOriginalName(string? company, string? product, string? original, bool accepted)
    {
        var reads = 0;
        var source = new WindowsAppRecentSource((_, _) => { reads++; return []; }, _ => new(company, product, original));
        Assert.Empty(source.ReadRecent(@"C:\Apps\Photoshop.exe", CancellationToken.None));
        Assert.Equal(accepted ? 1 : 0, reads);
    }

    [Fact]
    public void UnreadableExecutableMetadataDoesNotReadHistory()
    {
        var reads = 0;
        var source = new WindowsAppRecentSource((_, _) => { reads++; return []; },
            _ => throw new UnauthorizedAccessException());
        Assert.Empty(source.ReadRecent(@"C:\Apps\Photoshop.exe", CancellationToken.None));
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task SoftwareReplacedAtTheSamePathMustPassIdentityAgainBeforeOpen()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "luma-recent-revalidate-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var executable = System.IO.Path.Combine(directory, "Photoshop.exe");
            var document = System.IO.Path.Combine(directory, "fixture.psd");
            System.IO.File.WriteAllText(executable, "identity injected; never executed");
            System.IO.File.WriteAllText(document, "never opened");
            var replaced = false;
            var source = new WindowsAppRecentSource((_, _) => [new(document, DateTimeOffset.UtcNow)],
                path => replaced ? new(null, null, null) : AdobeMetadata(path));
            var store = new StateStore(System.IO.Path.Combine(directory, "state")); store.Load();
            var state = TestStates.OneProject("p", executable); state.Projects[0].Items[0].Kind = "app";
            store.Save(state, store.Current.Revision);
            var opens = 0;
            var service = new RecentProjectService(store, source, (_, _) => { opens++; return null; });
            var entry = Assert.Single((await service.GetAsync("dock", "p", "main")).Entries);
            replaced = true;
            Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<FolderOperationException>(() =>
                service.OpenAsync("dock", "p", "main", entry.Id))).Code);
            Assert.Equal(0, opens);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    private static WindowsAppRecentSource.ApplicationMetadata AdobeMetadata(string executable) =>
        new("Adobe Inc.", "Adobe " + System.IO.Path.GetFileNameWithoutExtension(executable) + " 2026", System.IO.Path.GetFileName(executable));
}
