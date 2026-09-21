using System.Windows;

namespace Luma.Host.Services;

/// <summary>Physical-position rearming; HWND enter/leave notifications alone are not proof of cursor movement.</summary>
internal sealed class HoverReentryGate
{
    private Rect? _dismissedRegion;
    public bool IsBlocked => _dismissedRegion is not null;
    public void SuppressAt(int x, int y, IEnumerable<Rect> hotspots)
    {
        _dismissedRegion = null;
        foreach (var region in hotspots)
            if (Contains(region, x, y)) { _dismissedRegion = region; break; }
    }
    public bool AllowsHoverAt(int x, int y)
    {
        if (_dismissedRegion is not { } region) return true;
        if (Contains(region, x, y)) return false;
        _dismissedRegion = null;
        return true;
    }
    public void Clear() => _dismissedRegion = null;
    // A queued timer/WM_MOUSELEAVE is not proof that the pointer is still here.
    // Observe a physical exit even when it rejects this dwell, so later reentry works.
    internal bool AllowsDwellAt(Rect hotspot, bool cursorAvailable, int x, int y, bool hotspotHit, bool pressedInput)
    {
        if (!cursorAvailable) return false;
        var reentryAllowed = AllowsHoverAt(x, y);
        return reentryAllowed && Contains(hotspot, x, y) && hotspotHit && !pressedInput;
    }
    private static bool Contains(Rect region, int x, int y) =>
        x >= region.Left && x < region.Right && y >= region.Top && y < region.Bottom;
}
