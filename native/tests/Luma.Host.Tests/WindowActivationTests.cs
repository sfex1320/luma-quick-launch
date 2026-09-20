using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

public class WindowActivationTests
{
    [Fact]
    public void AcceptedRequestStillRequiresObservedForegroundWindow()
    {
        var elapsed = 0;
        Assert.False(WindowActivation.RestoreAndActivate(() => false, () => { },
            () => true, () => false, ms => elapsed += ms));
        Assert.Equal(100, elapsed);
    }
    [Fact]
    public void WaitsForPostedRestoreBeforeForegroundRequest()
    {
        var elapsed = 0;
        var restored = false;
        var activationAt = -1;
        Assert.True(WindowActivation.RestoreAndActivate(() => elapsed < 60,
            () => restored = true, () => { activationAt = elapsed; return elapsed >= 60; },
            () => elapsed >= 60, ms => elapsed += ms));
        Assert.True(restored);
        Assert.InRange(activationAt, 60, 300);
    }

    [Fact]
    public void AsynchronousForegroundCompletionIsNotReportedAsDenied()
    {
        var elapsed = 0;
        Assert.True(WindowActivation.RestoreAndActivate(() => false, () => throw new Exception("Not minimized"),
            () => false, () => elapsed >= 40, ms => elapsed += ms));
        Assert.InRange(elapsed, 40, 150);
    }

    [Fact]
    public void DeniedForegroundAndHungRestoreHaveFiniteBudgets()
    {
        var elapsed = 0;
        var calls = 0;
        Assert.False(WindowActivation.RestoreAndActivate(() => true, () => { },
            () => { calls++; return false; }, () => false, ms => elapsed += ms));
        Assert.Equal(1, calls);
        Assert.InRange(elapsed, 300, 500);
    }

    [Fact]
    public void AcceptedForegroundCannotReportSuccessWhileWindowRemainsMinimized()
    {
        var elapsed = 0;
        Assert.False(WindowActivation.RestoreAndActivate(() => true, () => { },
            () => true, () => true, ms => elapsed += ms));
        Assert.Equal(300, elapsed);
    }

    [Fact]
    public void ExistingNonMinimizedWindowIsNotRestoredOrResized()
    {
        Assert.True(WindowActivation.RestoreAndActivate(() => false, () => throw new Exception("Unexpected restore"),
            () => true, () => true, _ => throw new Exception("Unexpected wait")));
    }
}
