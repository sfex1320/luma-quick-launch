using System.IO;
using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

public sealed class ShortcutImportServiceTests
{
    [Fact]
    public void ImportsActualFilesAndFoldersWithoutLaunchingAndRejectsInvalidPaths()
    {
        var directory = Path.Combine(Path.GetTempPath(), "luma-import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var files = new[] { "程序.exe", "链接.lnk", "发布.appref-ms", "说明.txt" }.Select(n => Path.Combine(directory, n)).ToArray();
            foreach (var file in files) File.WriteAllText(file, "");
            var paths = files.Concat(new[] { directory, files[0] }).ToArray();
            var result = new ShortcutImportService().Resolve(paths);
            Assert.Equal(5, result.Count);
            Assert.Equal(new[] { "app", "app", "app", "file", "folder" }, result.Select(r => r.Kind));
            Assert.All(result, r => Assert.True(File.Exists(r.Path) || Directory.Exists(r.Path)));
            foreach (var invalid in new[] { "https://example.com", "cmd /c calc", Path.Combine(directory, "missing") })
                Assert.Throws<ArgumentException>(() => new ShortcutImportService().Resolve(new[] { files[0], invalid }));
            Assert.Throws<ArgumentException>(() => new ShortcutImportService().Resolve(Enumerable.Repeat(directory, 101).ToArray()));
        }
        finally { Directory.Delete(directory, true); }
    }
}
