using System.Windows;
using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

public class HoverReentryGateTests
{
    private static readonly Rect Primary = new(640, 0, 640, 4);
    private static readonly Rect Secondary = new(-1600, 120, 960, 6);

    [Fact]
    public void CollapseUnderStationaryCursorIgnoresRepeatedSyntheticEnterLeaveAndDwell()
    {
        var gate = new HoverReentryGate();
        gate.SuppressAt(800, 0, new[] { Primary, Secondary });
        Assert.True(gate.IsBlocked);
        for (var i = 0; i < 8; i++) Assert.False(gate.AllowsHoverAt(800, 0));
        Assert.False(gate.AllowsHoverAt(1000, 3)); // Movement within the same hotspot is still not an exit.
    }

    [Fact]
    public void RealExitThenReentryRearmsWithoutResidentPolling()
    {
        var gate = new HoverReentryGate();
        gate.SuppressAt(800, 0, new[] { Primary });
        Assert.True(gate.AllowsHoverAt(800, 4)); // Win32 bottom edge is exclusive.
        Assert.False(gate.IsBlocked);
        Assert.True(gate.AllowsHoverAt(800, 0));
    }

    [Fact]
    public void SuppressionIsLocalToPhysicalMonitorRectangle()
    {
        var gate = new HoverReentryGate();
        gate.SuppressAt(-1000, 120, new[] { Primary, Secondary });
        Assert.False(gate.AllowsHoverAt(-1000, 125));
        Assert.True(gate.AllowsHoverAt(800, 0));
        Assert.True(gate.AllowsHoverAt(-1000, 120));
    }

    [Fact]
    public void CollapseOutsideHotspotDoesNotSuppressLaterHover()
    {
        var gate = new HoverReentryGate();
        gate.SuppressAt(800, 40, new[] { Primary });
        Assert.False(gate.IsBlocked);
        Assert.True(gate.AllowsHoverAt(800, 0));
    }

    [Fact]
    public void ExplicitActivationCanClearSuppressionWithoutMouseMovement()
    {
        var gate = new HoverReentryGate();
        gate.SuppressAt(800, 0, new[] { Primary });
        gate.Clear();
        Assert.True(gate.AllowsHoverAt(800, 0));
        gate.SuppressAt(800, 0, new[] { Primary });
        Assert.False(gate.AllowsHoverAt(800, 0));
    }
}
