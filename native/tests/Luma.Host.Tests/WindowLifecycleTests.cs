using System.Windows;
using Luma.Host.Services;
using Luma.Host.Windows;
using Xunit;
namespace Luma.Host.Tests;
public class WindowLifecycleTests
{
    [Fact]
    public void OrdinaryWebMessagesWithoutAdditionalObjectsHaveNoPaths()
    {
        Assert.Empty(WebMessageFiles.ExtractPaths(null));
        Assert.Empty(WebMessageFiles.ExtractPaths(Array.Empty<object>()));
        Assert.Empty(WebMessageFiles.ExtractPaths(new object[] { "C:\\untrusted-json-path", new object() }));
    }

    [Fact]
    public void QueuedCollapsedReportsCannotCancelReveal()
    {
        var state = new DockVisibilityState();
        state.RequestShow();
        for (var i = 0; i < 4; i++)
        {
            Assert.False(state.Acknowledge(false));
            Assert.False(state.ShouldHide(false));
            Assert.Equal(DockVisibilityPhase.AwaitingExpanded, state.Phase);
        }
        Assert.True(state.Acknowledge(true));
        Assert.Equal(DockVisibilityPhase.Visible, state.Phase);
        Assert.True(state.ShouldHide(false));
        state.Hide();
        Assert.False(state.Acknowledge(true));
        Assert.Equal(DockVisibilityPhase.Hidden, state.Phase);
    }
    [Fact]
    public void OldExpandedAndCollapsedPairCannotConfirmOrCloseNewReveal()
    {
        var state = new DockVisibilityState();
        Assert.Equal(0, state.VisibilityId);
        state.RequestShow();
        var oldShow = state.VisibilityId;
        Assert.True(state.Acknowledge(true, oldShow));
        state.RequestHide();
        var oldExit = state.VisibilityId;
        state.RequestShow();
        var newShow = state.VisibilityId;
        Assert.True(newShow > oldExit && oldExit > oldShow);
        foreach (var staleId in new[] { oldShow, oldExit })
        {
            Assert.False(state.AcceptsSync(staleId));
            Assert.False(state.Acknowledge(true, staleId));
            Assert.False(state.ShouldHide(false, staleId));
            Assert.Equal(DockVisibilityPhase.AwaitingExpanded, state.Phase);
        }
        Assert.True(state.AcceptsSync(newShow));
        Assert.True(state.Acknowledge(true, newShow));
        Assert.Equal(DockVisibilityPhase.Visible, state.Phase);
        Assert.False(state.ShouldHide(false, oldExit));
        Assert.True(state.ShouldHide(false, newShow));
    }

    [Fact]
    public void DisplayInvalidationAndCloseKeepOldInteractionReportsOut()
    {
        var state = new DockVisibilityState();
        state.RequestShow(); state.Acknowledge(true);
        var oldShow = state.VisibilityId;
        state.RequestImmediateHide();
        var displayHide = state.VisibilityId;
        Assert.Equal(DockVisibilityPhase.Hidden, state.Phase);
        Assert.True(displayHide > oldShow);
        Assert.False(state.AcceptsSync(oldShow)); // App applies interaction only after this guard.
        state.Hide();
        Assert.Equal(displayHide, state.VisibilityId); // final HWND hide emits no command
        state.RequestShow();
        Assert.False(state.AcceptsSync(displayHide));
        Assert.True(state.AcceptsSync(null)); // legacy optional protocol=1 field
        Assert.False(state.AcceptsSync(state.VisibilityId + 1));
    }

    [Fact]
    public void ExitWaitsForCollapsedLayoutAndCanReverse()
    {
        var state = new DockVisibilityState();
        state.RequestShow(); state.Acknowledge(true);
        Assert.True(state.RequestHide());
        Assert.Equal(DockVisibilityPhase.Closing, state.Phase);
        Assert.False(state.ShouldHide(true));
        Assert.True(state.ShouldHide(false));
        Assert.False(state.RequestHide());
        state.RequestShow();
        Assert.False(state.ShouldHide(false));
        Assert.False(state.Acknowledge(false));
        Assert.True(state.Acknowledge(true));
        Assert.Equal(DockVisibilityPhase.Visible, state.Phase);
    }

    [Fact]
    public void ExitFallbackIsBoundedAndOldDeadlineCannotHideReentry()
    {
        var state = new DockVisibilityState();
        state.RequestShow(); state.Acknowledge(true); state.RequestHide(1000);
        Assert.False(state.CloseExpired(1800)); // 600ms slide plus delayed rendering/bridge scheduling
        Assert.False(state.CloseExpired(2399));
        Assert.True(state.CloseExpired(2400));
        state.RequestShow();
        Assert.False(state.CloseExpired(1900));
        state.Acknowledge(true); state.RequestHide(2000);
        Assert.False(state.CloseExpired(2100)); // queued tick from the old close
        Assert.True(state.CloseExpired(3400));
        state.Hide();
        Assert.False(state.CloseExpired(9999));
    }

    [Theory]
    [InlineData(0x01)] [InlineData(0x02)] [InlineData(0x04)] [InlineData(0x05)] [InlineData(0x06)]
    [InlineData(0x10)] [InlineData(0x11)] [InlineData(0x12)] [InlineData(0x5B)] [InlineData(0x5C)]
    public void HeldButtonsAndModifiersPreventCollapseButPastPressDoesNot(int heldKey)
    {
        Assert.True(EdgeActivation.HasPressedInput(key => key == heldKey ? unchecked((short)0x8000) : (short)0));
        Assert.False(EdgeActivation.HasPressedInput(key => key == heldKey ? (short)1 : (short)0));
    }

    [Fact]
    public void NeverShownDockDoesNotStartExit()
    {
        var state = new DockVisibilityState();
        Assert.False(state.RequestHide());
        Assert.False(state.ShouldHide(false));
    }

    [Fact]
    public void RepeatedRevealRequiresFreshAcknowledgement()
    {
        var state = new DockVisibilityState();
        for (var i = 0; i < 10; i++)
        {
            state.RequestShow(); Assert.True(state.Acknowledge(true));
            Assert.False(state.Acknowledge(true)); state.Hide();
        }
    }
    [Theory]
    [InlineData(-2560, -240, 2560, 1440, 1.5, -1730, 900)]
    [InlineData(1920, 0, 3840, 2160, 2, 3240, 1200)]
    public void HotspotKeepsPhysicalOriginOnMixedDpiMonitors(int x, int y, int width, int height, double scale, int expectedX, int expectedWidth)
    {
        var monitor = new MonitorInfo { Handle = new IntPtr(2), PhysicalWorkArea = new Rect(x,y,width,height), DpiScale = scale };
        var layout = App.CreateHotspotLayout(monitor, 600);
        Assert.Equal(expectedX, layout.X); Assert.Equal(y, layout.Y);
        Assert.Equal(expectedWidth, layout.Width); Assert.Equal(monitor.Handle, layout.Monitor);
    }
    [Theory]
    [InlineData("Progman", 0, false)]
    [InlineData("WorkerW", 0, false)]
    [InlineData("Shell_TrayWnd", 0, false)]
    [InlineData("GameWindow", 0, true)]
    [InlineData("NormalWindow", 12582912, false)]
    public void DesktopAndMaximizedCaptionedWindowsAreNotFullscreen(string name, long style, bool expected)
    {
        var monitor = new Rect(-1920,0,1920,1080);
        Assert.Equal(expected, EdgeActivation.IsFullscreenWindow(name,style,monitor,monitor));
        Assert.False(EdgeActivation.IsFullscreenWindow(name,style,new Rect(-1920,0,1800,1000),monitor));
    }
    [Fact]
    public void ShadowHaloIsBoundedAroundPanel()
    {
        var halo = BackdropService.ShadowBounds(new Rect(600,0,600,100),1.5);
        Assert.Equal(new Rect(858,-42,984,246),halo);
        Assert.True(halo.Width < 1920);
    }
}
