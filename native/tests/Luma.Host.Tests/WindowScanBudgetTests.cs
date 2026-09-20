using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

public class WindowScanBudgetTests
{
    [Fact]
    public void HiddenDesktopWindowsDoNotConsumeVisibleCandidateBudget()
    {
        var budget = new WindowScanBudget(() => TimeSpan.Zero);
        for (var hidden = 0; hidden < 2000; hidden++) Assert.True(budget.VisitWindow());
        Assert.True(budget.VisitWindow());
        Assert.True(budget.VisitCandidate());
    }

    [Fact]
    public void RawTraversalStillStopsAt8192Windows()
    {
        var budget = new WindowScanBudget(() => TimeSpan.Zero);
        for (var window = 0; window < 8192; window++) Assert.True(budget.VisitWindow());
        Assert.False(budget.VisitWindow());
    }

    [Fact]
    public void ExpensiveCandidatesStopAt512AndElapsedTimeStopsTraversal()
    {
        var elapsed = TimeSpan.Zero;
        var budget = new WindowScanBudget(() => elapsed);
        for (var candidate = 0; candidate < 512; candidate++) Assert.True(budget.VisitCandidate());
        Assert.False(budget.VisitCandidate());
        elapsed = TimeSpan.FromMilliseconds(1001);
        Assert.False(budget.VisitWindow());
    }
}
