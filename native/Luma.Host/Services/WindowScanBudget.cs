using System.Diagnostics;

namespace Luma.Host.Services;

internal sealed class WindowScanBudget(Func<TimeSpan>? elapsed = null)
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private int _windows;
    private int _candidates;
    public bool VisitWindow() => ++_windows <= 8192 && (elapsed?.Invoke() ?? _watch.Elapsed) <= TimeSpan.FromSeconds(1);
    public bool VisitCandidate() => ++_candidates <= 512;
}
