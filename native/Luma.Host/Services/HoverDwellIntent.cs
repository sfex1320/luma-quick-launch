namespace Luma.Host.Services;

/// <summary>A relocated/revealed HWND is not mouse intent, even if Windows emits WM_MOUSEMOVE.</summary>
internal sealed class HoverMovementGate
{
    private bool _available;
    private int _x, _y;

    public void Reset(bool available, int x, int y)
    { _available = available; _x = x; _y = y; }

    public bool Observe(bool available, int x, int y, bool sourceAvailable, int device, int origin)
    {
        var moved = available && _available && (x != _x || y != _y);
        Reset(available, x, y);
        // INPUT_MESSAGE_DEVICE_TYPE.IMDT_MOUSE / INPUT_MESSAGE_ORIGIN_ID.IMO_HARDWARE.
        // This filters synthetic events; it does not prove who moved the pointer.
        return moved && sourceAvailable && device == 2 && origin == 1;
    }
}

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
