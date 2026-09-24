using System.Reflection;
using System.Windows;
using Luma.Host.Interop;
using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

public class HotspotWakeIntentTests
{
    [Theory]
    [InlineData(true, 800, 4, true, false)] // Physical exit before queued WM_MOUSELEAVE.
    [InlineData(true, 1280, 0, true, false)] // Win32 right edge is exclusive.
    [InlineData(true, 800, -1, true, false)]
    [InlineData(true, 800, 0, false, false)] // Another window now covers the hotspot.
    [InlineData(false, 800, 0, true, false)] // Failed cursor read must not reveal.
    [InlineData(true, 800, 0, true, true)] // Ordinary drag / held input is not hover intent.
    public void ExpiredDwellRequiresCurrentPhysicalHover(bool available, int x, int y, bool hit, bool pressed)
    {
        var gate = new HoverReentryGate();
        Assert.False(gate.AllowsDwellAt(new Rect(640, 0, 640, 4), available, x, y, hit, pressed));
    }

    [Fact]
    public void PhysicalHoverStillRevealsOnNegativeOriginScaledMonitor()
    {
        var gate = new HoverReentryGate();
        Assert.True(gate.AllowsDwellAt(new Rect(-1600, 120, 960, 6), true, -1000, 125, true, false));
        Assert.False(gate.AllowsDwellAt(new Rect(-1600, 120, 960, 6), true, -1000, 126, true, false));
    }

    [Fact]
    public void DwellCannotUndoExplicitCollapseUntilPhysicalExit()
    {
        var gate = new HoverReentryGate();
        var bounds = new Rect(640, 0, 640, 4);
        gate.SuppressAt(800, 0, new[] { bounds });
        Assert.False(gate.AllowsDwellAt(bounds, true, 800, 0, true, false));
        Assert.False(gate.AllowsDwellAt(bounds, true, 800, 4, false, false));
        Assert.True(gate.AllowsDwellAt(bounds, true, 800, 0, true, false));
    }

    [Fact]
    public void MouseReleaseWithoutHotspotPressDoesNotReveal()
    {
        using var hotspot = new NativeHotspotWindow(); // Registers a class; no HWND or GUI is created.
        var reveals = 0;
        hotspot.Activated += (_, _) => reveals++;
        Dispatch(hotspot, 0x0202);
        Assert.Equal(0, reveals);
    }

    [Theory]
    [InlineData(0x02A3)] // WM_MOUSELEAVE.
    [InlineData(0x001F)] // WM_CANCELMODE.
    [InlineData(0x0215)] // WM_CAPTURECHANGED.
    public void CancelledHotspotPressCannotBeCompletedByLaterRelease(int cancellation)
    {
        using var hotspot = new NativeHotspotWindow();
        var reveals = 0;
        hotspot.Activated += (_, _) => reveals++;
        Dispatch(hotspot, 0x0201);
        Dispatch(hotspot, cancellation);
        Dispatch(hotspot, 0x0202);
        Assert.Equal(0, reveals);
    }

    [Fact]
    public void CompletedHotspotClickRevealsOnceAndHideCancelsOldPress()
    {
        using var hotspot = new NativeHotspotWindow();
        var reveals = 0;
        hotspot.Activated += (_, _) => reveals++;
        Dispatch(hotspot, 0x0201);
        Dispatch(hotspot, 0x0202);
        Dispatch(hotspot, 0x0202);
        Assert.Equal(1, reveals);
        Dispatch(hotspot, 0x0201);
        hotspot.HideWindow();
        Dispatch(hotspot, 0x0202);
        Assert.Equal(1, reveals);
    }

    private static void Dispatch(NativeHotspotWindow hotspot, int message) =>
        typeof(NativeHotspotWindow).GetMethod("InstanceWndProc", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(hotspot, new object[] { IntPtr.Zero, message, IntPtr.Zero, IntPtr.Zero });

    [Fact]
    public void MovementWithinOneHotspotContinuesToNotifyStabilityTracker()
    {
        using var hotspot = new NativeHotspotWindow();
        var enters = 0; var moves = 0;
        hotspot.CursorEnter += (_, _) => enters++;
        hotspot.CursorMoved += (_, _) => moves++;
        Dispatch(hotspot, 0x0200);
        Dispatch(hotspot, 0x0200);
        Assert.Equal(1, enters);
        Assert.Equal(2, moves);
        Dispatch(hotspot, 0x02A3);
        Dispatch(hotspot, 0x0200);
        Assert.Equal(2, enters);
        Assert.Equal(3, moves);
    }
}
