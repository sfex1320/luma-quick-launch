using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

public class HoverDwellIntentTests
{
    [Fact]
    public void Reported112720SweepMustNotCompleteAnEntryOnlyTimer()
    {
        var dwell = new HoverDwellIntent();
        dwell.Observe(2196, 0, 1000);
        dwell.Observe(1995, 2, 1175);
        Assert.False(dwell.IsReadyAt(1995, 2, 1190));
        Assert.False(dwell.IsReadyAt(1995, 2, 1354));
        Assert.True(dwell.IsReadyAt(1995, 2, 1355));
    }

    [Fact]
    public void ContinuousTraversalNeverRevealsUntilPointerSettles()
    {
        var dwell = new HoverDwellIntent();
        for (var t = 0; t < 2000; t += 30)
        {
            dwell.Observe(1500 + t / 10, 0, t);
            Assert.False(dwell.IsReadyAt(1500 + t / 10, 0, t));
        }
        Assert.True(dwell.IsReadyAt(1698, 0, 2160));
    }

    [Fact]
    public void FinalPhysicalSampleCatchesMovementBeforeQueuedMoveMessage()
    {
        var dwell = new HoverDwellIntent();
        dwell.Observe(800, 0, 1000);
        Assert.False(dwell.IsReadyAt(900, 0, 1180));
        Assert.True(dwell.IsReadyAt(900, 0, 1360));
    }

    [Fact]
    public void SmallJitterDoesNotPreventDeliberateHover()
    {
        var dwell = new HoverDwellIntent();
        dwell.Observe(-1500, 120, 1000);
        dwell.Observe(-1498, 122, 1100);
        Assert.False(dwell.IsReadyAt(-1500, 120, 1179));
        Assert.True(dwell.IsReadyAt(-1500, 120, 1180));
    }

    [Fact]
    public void LeaveOrRelocationCannotReusePreviousStableTime()
    {
        var dwell = new HoverDwellIntent();
        dwell.Observe(800, 0, 1000);
        dwell.Reset();
        Assert.False(dwell.IsReadyAt(800, 0, 2000));
        Assert.True(dwell.IsReadyAt(800, 0, 2180));
    }
}
