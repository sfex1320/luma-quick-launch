using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

public class ActivationDiagnosticsTests
{
    [Fact]
    public void Win32InputAgeHandlesTickWraparound()
    {
        Assert.Equal(32u, ActivationDiagnostics.Age(16, uint.MaxValue - 15));
        Assert.Equal(180u, ActivationDiagnostics.Age(1000, 820));
    }

    [Fact]
    public void DiagnosticSnapshotReadsWindowsWithoutCreatingOrActivatingAWindow()
    {
        var snapshot = ActivationDiagnostics.Capture();
        Assert.DoesNotContain("diagnosticUnavailable", snapshot);
        Assert.Contains("physical=", snapshot);
        Assert.Contains("cursorFlags=", snapshot);
        Assert.Contains("inputAgeMs=", snapshot);
        Assert.Contains("lastInputTick=", snapshot);
        Assert.Contains("inputSource=", snapshot);
        Assert.Contains("hotspot=0x0 actualRect=unavailable", snapshot);
    }
}
