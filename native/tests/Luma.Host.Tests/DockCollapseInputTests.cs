using Luma.Host.Services;
using Luma.Host.Windows;
using Xunit;

namespace Luma.Host.Tests;

public class DockCollapseInputTests
{
    [Theory]
    [InlineData(0x01)] // Clicking another window's minimize button.
    [InlineData(0x12)] // Alt+Tab.
    [InlineData(0x5B)] // Win+Down.
    [InlineData(0x5C)]
    public void OtherWindowInputCannotReverseAnExitOrInvalidateItsCollapsedAcknowledgement(int key)
    {
        var state = ClosingDock();
        var exitId = state.VisibilityId;
        var pressed = EdgeActivation.HasPressedInput(value => value == key ? unchecked((short)0x8000) : (short)0);

        if (EdgeActivation.InputKeepsDockOpen(state.Phase == DockVisibilityPhase.Closing, false, pressed))
            state.RequestShow();

        Assert.Equal(DockVisibilityPhase.Closing, state.Phase);
        Assert.True(state.ShouldHide(false, exitId));
        Assert.Equal(exitId, state.VisibilityId);
    }

    [Fact]
    public void HeldExternalInputCannotDefeatTheBoundedCloseFallback()
    {
        var state = ClosingDock();
        Assert.True(state.CloseExpired(2400));

        if (EdgeActivation.InputKeepsDockOpen(closing: true, interacting: false, pressedInput: true))
            state.RequestShow();
        if (state.Phase == DockVisibilityPhase.Closing) state.Hide();

        Assert.Equal(DockVisibilityPhase.Hidden, state.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualDockInteractionCanReverseClosingAndFenceOldAcknowledgements(bool pressedInput)
    {
        var state = ClosingDock();
        var exitId = state.VisibilityId;
        if (EdgeActivation.InputKeepsDockOpen(closing: true, interacting: true, pressedInput))
            state.RequestShow();

        Assert.Equal(DockVisibilityPhase.AwaitingExpanded, state.Phase);
        Assert.False(state.AcceptsSync(exitId));
        Assert.False(state.CloseExpired(2400));
    }

    [Fact]
    public void HeldInputStillKeepsAnExpandedDockOpen()
    {
        Assert.True(EdgeActivation.InputKeepsDockOpen(closing: false, interacting: false, pressedInput: true));
        Assert.False(EdgeActivation.InputKeepsDockOpen(closing: false, interacting: false, pressedInput: false));
    }

    private static DockVisibilityState ClosingDock()
    {
        var state = new DockVisibilityState();
        state.RequestShow();
        state.Acknowledge(true, state.VisibilityId);
        state.RequestHide(1000);
        return state;
    }
}
