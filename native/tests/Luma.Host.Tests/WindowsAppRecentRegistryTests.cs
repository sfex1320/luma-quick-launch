using System.Runtime.InteropServices;
using System.Text;
using Luma.Host.Services;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Luma.Host.Tests;

public sealed class WindowsAppRecentRegistryTests : IDisposable
{
    private readonly string _path = @"Software\Luma.Tests\Recent-" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    public WindowsAppRecentRegistryTests() => _root = Registry.CurrentUser.CreateSubKey(_path);
    public void Dispose()
    {
        _root.Dispose();
        // The sole deletion target is the exact GUID fixture key created by this instance.
        Registry.CurrentUser.DeleteSubKeyTree(_path, false);
    }

    [Theory]
    [InlineData("2026-07-24T06:54:48.70924Z")]
    [InlineData("2026-09-20T01:59:13.4502Z")]
    [InlineData("2026-09-20T06:13:37.209159Z")]
    public void ReadsObservedAdobeTimestampFormatsAndDefaultString(string timestamp)
    {
        using var row = _root.CreateSubKey(timestamp);
        row.SetValue("", @"C:\Art\fixture.psd", RegistryValueKind.String);
        var found = Assert.Single(WindowsAppRecentSource.ReadRegistryRows(_root, CancellationToken.None));
        Assert.Equal(@"C:\Art\fixture.psd", found.Path);
        Assert.Equal(DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture), found.Modified);
        Assert.Equal(TimeSpan.Zero, found.Modified.Offset);
    }

    [Fact]
    public void RegistryEnumerationStopsAfter256NamesBeforeFiltering()
    {
        for (var i = 0; i < 300; i++)
        {
            using var row = _root.CreateSubKey(DateTimeOffset.UnixEpoch.AddSeconds(i).ToString("O"));
            row.SetValue("", $@"C:\Art\{i}.psd", RegistryValueKind.String);
        }
        var expected = _root.GetSubKeyNames().Take(256).Select(name => DateTimeOffset.Parse(name)).ToHashSet();
        var rows = WindowsAppRecentSource.ReadRegistryRows(_root, CancellationToken.None).ToArray();
        Assert.Equal(256, rows.Length);
        Assert.All(rows, row => Assert.Contains(row.Modified, expected));
        // Invalidate the names' contents: the reader must not continue scanning to replace skipped values.
        foreach (var name in _root.GetSubKeyNames().Take(256))
        { using var row = _root.OpenSubKey(name, true); row!.DeleteValue(""); }
        Assert.Empty(WindowsAppRecentSource.ReadRegistryRows(_root, CancellationToken.None));
    }

    [Theory]
    [InlineData(4096, true)]
    [InlineData(4097, false)]
    [InlineData(1000000, false)]
    public void StringSizeIsCheckedBeforeReadingTheValue(int length, bool accepted)
    {
        _root.SetValue("", new string('x', length), RegistryValueKind.String);
        var value = WindowsAppRecentSource.ReadRegistryPath(_root, CancellationToken.None);
        if (accepted) Assert.Equal(length, value!.Length); else Assert.Null(value);
    }

    [Theory]
    [InlineData(1, "C:\\Art\\fixture.psd", true)]
    [InlineData(1, "C:\\Art\\fixture.psd\0", true)]
    [InlineData(1, "C:\\Art\\fixture.psd\0\0", false)]
    [InlineData(1, "C:\\Art\\fix\0ture.psd", false)]
    [InlineData(2, "%TEMP%\\fixture.psd\0", false)]
    [InlineData(3, "C:\\Art\\fixture.psd\0", false)]
    public void AcceptsOnlyLiteralSingleStringWithOptionalTerminator(uint type, string content, bool accepted)
    {
        var bytes = Encoding.Unicode.GetBytes(content);
        Assert.Equal(0, RegSetValueEx(_root.Handle, null, 0, type, bytes, (uint)bytes.Length));
        var value = WindowsAppRecentSource.ReadRegistryPath(_root, CancellationToken.None);
        if (accepted) Assert.Equal(content.TrimEnd('\0'), value); else Assert.Null(value);
    }

    [Fact]
    public void MissingValueAndLargeBinaryPayloadAreNotReadAsPaths()
    {
        Assert.Null(WindowsAppRecentSource.ReadRegistryPath(_root, CancellationToken.None));
        _root.SetValue("", new byte[1024 * 1024], RegistryValueKind.Binary);
        Assert.Null(WindowsAppRecentSource.ReadRegistryPath(_root, CancellationToken.None));
    }

    [Fact]
    public void RejectsOddByteCountAndInvalidUtf16()
    {
        Assert.Equal(0, RegSetValueEx(_root.Handle, null, 0, 1, [65], 1));
        Assert.Null(WindowsAppRecentSource.ReadRegistryPath(_root, CancellationToken.None));
        Assert.Equal(0, RegSetValueEx(_root.Handle, null, 0, 1, [0, 0xD8], 2));
        Assert.Null(WindowsAppRecentSource.ReadRegistryPath(_root, CancellationToken.None));
    }

    [Fact]
    public void CancelledRegistryReadDoesNotContinueEnumerating()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => WindowsAppRecentSource.ReadRegistryRows(_root, cancellation.Token).ToArray());
        Assert.Throws<OperationCanceledException>(() => WindowsAppRecentSource.ReadRegistryPath(_root, cancellation.Token));
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegSetValueExW")]
    private static extern int RegSetValueEx(SafeRegistryHandle key, string? name, uint reserved, uint type, byte[] data, uint bytes);
}
