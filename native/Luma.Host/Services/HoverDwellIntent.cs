namespace Luma.Host.Services;

/// <summary>Hover means remaining near one physical point, not traversing a wide edge strip.</summary>
internal sealed class HoverDwellIntent
{
    private long? _since;
    private int _x, _y;
    internal const int TolerancePx = 3;
    public bool Observe(int x, int y, long now)
    {
        // Compare with the anchor, not the last sample: slow accumulated drift is movement too.
        if (_since.HasValue && now >= _since.Value &&
            Math.Abs((long)x - _x) <= TolerancePx && Math.Abs((long)y - _y) <= TolerancePx) return false;
        _x = x; _y = y;
        _since = now;
        return true;
    }
    public bool IsReadyAt(int x, int y, long now)
    {
        Observe(x, y, now);
        return now - _since!.Value >= EdgeActivation.DwellDelay.TotalMilliseconds;
    }
    public void Reset() => _since = null;
}
