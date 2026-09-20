using System.Runtime.InteropServices;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using Luma.Host.Services;
using Microsoft.Win32;
using Xunit;

namespace Luma.Host.Tests;

public sealed class RuntimePreflightTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("124.0.2478.67", true)]
    [InlineData("not-a-version", false)]
    public void Runtime_version_must_be_a_nonzero_version(string value, bool expected)
    {
        Assert.Equal(expected, RuntimePreflight.IsUsableRuntimeVersion(value));
    }

    [Fact]
    public void Detects_machine_runtime_in_32_bit_registry_view_on_x64_windows()
    {
        var registry = new RecordingRegistryReader(new Dictionary<(RegistryHive, RegistryView, string), string?>
        {
            [(RegistryHive.LocalMachine, RegistryView.Registry32, RuntimePreflight.ClientRegistryPath)] = "124.0.2478.67"
        });

        Assert.True(RuntimePreflight.IsWebView2Installed(registry, is64BitOperatingSystem: true));
        Assert.Contains(registry.Reads, read => read.Hive == RegistryHive.LocalMachine && read.View == RegistryView.Registry32);
    }

    [Fact]
    public void Detects_per_user_runtime_without_requiring_machine_install()
    {
        var registry = new RecordingRegistryReader(new Dictionary<(RegistryHive, RegistryView, string), string?>
        {
            [(RegistryHive.CurrentUser, RegistryView.Default, RuntimePreflight.ClientRegistryPath)] = "125.0.0.1"
        });

        Assert.True(RuntimePreflight.IsWebView2Installed(registry, is64BitOperatingSystem: true));
    }

    [Theory]
    [InlineData(10, 0, 19044, false)]
    [InlineData(10, 0, 19045, true)]
    [InlineData(10, 0, 22631, true)]
    [InlineData(6, 3, 9600, false)]
    public void Requires_Windows_10_22H2_or_newer(int major, int minor, int build, bool expected)
    {
        Assert.Equal(expected, RuntimePreflight.IsSupportedWindows(new Version(major, minor, build)));
    }

    [Fact]
    public void Release_is_x64_only()
    {
        Assert.True(RuntimePreflight.IsSupportedArchitecture(Architecture.X64));
        Assert.False(RuntimePreflight.IsSupportedArchitecture(Architecture.Arm64));
        Assert.False(RuntimePreflight.IsSupportedArchitecture(Architecture.X86));
    }

    [Fact]
    public void Runtime_install_retries_once_then_accepts_detected_runtime()
    {
        var launches = 0;
        var result = RuntimePreflight.InstallWithRetry(
            () => { launches++; return launches == 1 ? 1 : 0; },
            () => launches == 2);

        Assert.True(result.Succeeded);
        Assert.Equal(2, launches);
        Assert.Equal([1, 0], result.ExitCodes);
    }

    [Fact]
    public void Runtime_install_failure_preserves_exit_codes_for_diagnostics()
    {
        var result = RuntimePreflight.InstallWithRetry(() => 5, () => false);

        Assert.False(result.Succeeded);
        Assert.Equal([5, 5], result.ExitCodes);
    }

    [Fact]
    public void Runtime_install_timeout_does_not_start_a_second_installer()
    {
        var launches = 0;
        var result = RuntimePreflight.InstallWithRetry(() => { launches++; return RuntimePreflight.InstallTimedOut; }, () => false);

        Assert.False(result.Succeeded);
        Assert.Equal(1, launches);
        Assert.Equal([RuntimePreflight.InstallTimedOut], result.ExitCodes);
    }

    [Fact]
    public void Rejects_unsigned_bootstrapper_before_execution()
    {
        var path = Path.GetTempFileName();
        try
        {
            Assert.False(RuntimePreflight.HasTrustedMicrosoftSignature(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("CN=WebView Setup, O=Microsoft Corporation", true)]
    [InlineData("CN=WebView Setup, O=Microsoft Corporation Services", false)]
    [InlineData("CN=O=Microsoft Corporation, O=Unrelated Publisher", false)]
    public void Microsoft_publisher_requires_an_exact_organization_attribute(string distinguishedName, bool expected)
    {
        var subject = new X500DistinguishedName(distinguishedName);

        Assert.Equal(expected, RuntimePreflight.HasMicrosoftPublisher(subject));
    }

    private sealed class RecordingRegistryReader(Dictionary<(RegistryHive, RegistryView, string), string?> values) : IRegistryValueReader
    {
        public List<(RegistryHive Hive, RegistryView View, string Path)> Reads { get; } = [];

        public string? ReadString(RegistryHive hive, RegistryView view, string subKey, string valueName)
        {
            Reads.Add((hive, view, subKey));
            return values.GetValueOrDefault((hive, view, subKey));
        }
    }
}
