using System.Windows;
using System.Windows.Threading;
using Luma.Host.Interop;

namespace Luma.Host.Services;

/// <summary>Per-monitor, event-driven top-edge activation. Only an acknowledged dock starts collapse checks.</summary>
public sealed class EdgeActivation : IDisposable
{
    public const int HotspotHeightPx = 4;
    public static readonly TimeSpan DwellDelay = TimeSpan.FromMilliseconds(180);
    public static readonly TimeSpan CollapseCheckInterval = TimeSpan.FromMilliseconds(150);
    public const int CollapseMissThreshold = 5;
    public static readonly TimeSpan FullscreenCheckInterval = TimeSpan.FromSeconds(1);
    private readonly DispatcherTimer _dwellTimer, _collapseTimer, _fullscreenTimer;
    private readonly Dictionary<IntPtr, (NativeHotspotWindow Window, HotspotLayout Layout)> _hotspots = new();
    private readonly Func<IntPtr> _dockHandle;
    private readonly Func<IReadOnlyList<HotspotLayout>> _layoutProvider;
    private readonly Func<bool> _keepOpen;
    private readonly Func<int, int, bool> _dockContainsPoint;
    private readonly HoverReentryGate _hoverReentry = new();
    private HotspotLayout? _active, _hover;
    private IntPtr _fullscreenMonitor;
    private int _collapseMisses;
    private bool _closing;
    private long _graceUntil;
    public event Action<HotspotLayout>? ShowDockRequested;
    public event Action? HideDockRequested;
    public event Action<bool>? FullscreenChanged;
    public bool Paused { get; private set; }
    public bool DockVisible { get; private set; }
    public bool FullscreenAvoiding => _fullscreenMonitor != IntPtr.Zero;
    public bool AutoCollapseEnabled { get; set; } = true;
    public sealed record HotspotLayout(int X, int Y, int Width, int Height, IntPtr Monitor, Rect WorkArea, double Scale);

    public EdgeActivation(Func<IntPtr> dockHandle, Func<IReadOnlyList<HotspotLayout>> layoutProvider,
        Func<bool>? keepOpen = null, Func<int, int, bool>? dockContainsPoint = null)
    {
        _dockHandle = dockHandle; _layoutProvider = layoutProvider; _keepOpen = keepOpen ?? (() => false);
        _dockContainsPoint = dockContainsPoint ?? ((_, _) => false);
        _dwellTimer = new DispatcherTimer { Interval = DwellDelay };
        _dwellTimer.Tick += (_, _) =>
        {
            _dwellTimer.Stop();
            var hover = _hover;
            _hover = null;
            Log.Info($"热区驻留到期 hover={hover?.Monitor} visible={DockVisible}");
            if (hover is null || Paused || !_hotspots.TryGetValue(hover.Monitor, out var current) || current.Layout != hover) return;
            var available = Win32.GetCursorPos(out var point);
            var hit = available ? Win32.WindowFromPoint(point) : IntPtr.Zero;
            var pressed = HasPressedInput();
            var allowed = _hoverReentry.AllowsDwellAt(new Rect(hover.X, hover.Y, hover.Width, hover.Height),
                available, point.X, point.Y, hit == current.Window.Handle, pressed);
            Log.Info($"驻留判定 allowed={allowed} sampledAvailable={available} sampledCursor={point.X},{point.Y} sampledHit=0x{hit:X} pressed={pressed} expectedRect={hover.X},{hover.Y},{hover.Width},{hover.Height} {ActivationDiagnostics.Capture(current.Window.Handle)}");
            if (allowed) RequestShow(hover, "hover-dwell");
        };
        _collapseTimer = new DispatcherTimer { Interval = CollapseCheckInterval };
        _collapseTimer.Tick += (_, _) => CheckCollapse();
        _fullscreenTimer = new DispatcherTimer { Interval = FullscreenCheckInterval };
        _fullscreenTimer.Tick += (_, _) => CheckFullscreen();
        _fullscreenTimer.Start();
    }
    public void Start() => RelocateHotspot();
    public void SetPaused(bool paused) { Paused = paused; _dwellTimer.Stop(); UpdateHotspotVisibility(); }
    public void TriggerFromTray(string reason = "explicit")
    {
        var layouts = _layoutProvider();
        if (layouts.Count == 0) return;
        Win32.GetCursorPos(out var point);
        _graceUntil = Environment.TickCount64 + 3000;
        RequestShow(layouts.FirstOrDefault(x => x.WorkArea.Contains(point.X, point.Y)) ?? layouts[0], reason);
    }
    private void RequestShow(HotspotLayout layout, string reason)
    {
        Log.Info($"浮岛唤出请求 monitor=0x{layout.Monitor:X} visible={DockVisible} active={_active?.Monitor}");
        Log.Info($"唤出来源 reason={reason} closing={_closing} {ActivationDiagnostics.Capture(_hotspots.TryGetValue(layout.Monitor, out var hotspot) ? hotspot.Window.Handle : IntPtr.Zero)}");
        CheckFullscreen(logTarget: true);
        if (_fullscreenMonitor != IntPtr.Zero && layout.Monitor == _fullscreenMonitor) { Log.Info("浮岛唤出被对应屏全屏应用抑制"); return; }
        if (DockVisible)
        {
            if (_active?.Monitor == layout.Monitor && !_closing) return;
        }
        _active = layout; DockVisible = true; _closing = false; _collapseMisses = 0;
        // Explicit click, OLE, tray and activation signals bypass hover suppression.
        // Hover callers have already passed the physical reentry guard.
        _hoverReentry.Clear();
        ShowDockRequested?.Invoke(layout);
    }
    public void NotifyDockShown() { Log.Info("浮岛已确认显示，启动离开检测"); if (DockVisible) _collapseTimer.Start(); }
    public void NotifyDockHidden() { DockVisible = false; _closing = false; _collapseTimer.Stop(); _collapseMisses = 0; }
    public void SuppressHoverAfterFrontendCollapse()
    {
        _dwellTimer.Stop(); _hover = null;
        if (!Win32.GetCursorPos(out var point)) return;
        _hoverReentry.SuppressAt(point.X, point.Y, _hotspots.Values.Select(p =>
            new Rect(p.Layout.X, p.Layout.Y, p.Layout.Width, p.Layout.Height)));
        if (_hoverReentry.IsBlocked) Log.Info($"主动收起后等待鼠标真实离开热区 cursor={point.X},{point.Y}");
    }
    private bool AllowsHoverAtCursor()
    {
        if (!Win32.GetCursorPos(out var point)) return !_hoverReentry.IsBlocked;
        var blocked = _hoverReentry.IsBlocked;
        var allowed = _hoverReentry.AllowsHoverAt(point.X, point.Y);
        if (blocked && allowed) Log.Info($"鼠标已真实离开收起热区，恢复悬停唤出 cursor={point.X},{point.Y}");
        return allowed;
    }
    public void CancelClose(string reason = "interaction")
    {
        if (_closing && _active is { } layout) RequestShow(layout, $"reverse:{reason}");
    }
    private void RequestHide()
    {
        if (_closing || !DockVisible) return;
        _closing = true;
        HideDockRequested?.Invoke();
    }
    public static bool HasPressedInput() => HasPressedInput(Win32.GetAsyncKeyState);
    internal static bool HasPressedInput(Func<int, short> keyState) => InteractionKeys.Any(key => (keyState(key) & 0x8000) != 0);
    // Mouse buttons + Shift / Ctrl / Alt / both Windows keys; no low-bit historical
    // press flag or keyboard hook. Hidden docks read once at hover dwell expiry;
    // only visible docks perform the existing bounded collapse checks.
    private static readonly int[] InteractionKeys = { 0x01, 0x02, 0x04, 0x05, 0x06, 0x10, 0x11, 0x12, 0x5B, 0x5C };
    // Global input can delay starting auto-hide, but cannot reverse an exit: Alt+Tab,
    // Win+Down and clicks in other apps are not interactions with this dock.
    // Actual dock interaction and the physical pointer reentry checks still reverse it.
    internal static bool InputKeepsDockOpen(bool closing, bool interacting, bool pressedInput) =>
        interacting || (!closing && pressedInput);
    private void CheckCollapse()
    {
        if (!AutoCollapseEnabled || InputKeepsDockOpen(_closing, _keepOpen(), HasPressedInput()) || Environment.TickCount64 < _graceUntil)
        { _collapseMisses = 0; CancelClose("keep-open"); return; }
        if (!Win32.GetCursorPos(out var point)) return;
        if (_dockContainsPoint(point.X, point.Y) || _hotspots.Values.Any(x =>
                new Rect(x.Layout.X, x.Layout.Y, x.Layout.Width, x.Layout.Height).Contains(point.X, point.Y)))
        { _collapseMisses = 0; CancelClose("physical-region"); return; }
        var hit = Win32.WindowFromPoint(point);
        var dock = _dockHandle();
        var cursor = hit;
        for (var i = 0; cursor != IntPtr.Zero && i < 16; i++, cursor = Win32.GetParent(cursor))
            if (cursor == dock) { _collapseMisses = 0; CancelClose("window-hit"); return; }
        if (_hotspots.Values.Any(x => x.Window.Handle == hit)) { _collapseMisses = 0; CancelClose("hotspot-hit"); return; }
        if (++_collapseMisses < CollapseMissThreshold) return;
        RequestHide();
    }
    public static bool IsFullscreenWindow(string className, long style, Rect window, Rect monitor) =>
        className is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") &&
        (style & 0x00C00000) == 0 && window.Left <= monitor.Left && window.Top <= monitor.Top &&
        window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;

    private static IntPtr DetectFullscreenMonitor(out string details)
    {
        details = "foreground unavailable";
        var foreground = Win32.GetForegroundWindow();
        if (foreground == IntPtr.Zero || !Win32.GetWindowRect(foreground, out var rect)) return IntPtr.Zero;
        var name = new System.Text.StringBuilder(256);
        Win32.GetClassName(foreground, name, name.Capacity);
        var monitor = Win32.MonitorFromWindow(foreground, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        static Rect R(Win32.RECT r) => new(r.Left, r.Top, Math.Max(0, r.Right-r.Left), Math.Max(0, r.Bottom-r.Top));
        details = $"hwnd=0x{foreground:X} class={name} monitor=0x{monitor:X} rect={R(rect)}";
        return IsFullscreenWindow(name.ToString(), Win32.GetWindowLong(foreground, Win32.GWL_STYLE), R(rect), R(info.rcMonitor)) ? monitor : IntPtr.Zero;
    }
    private void CheckFullscreen(bool logTarget = false)
    {
        var monitor = DetectFullscreenMonitor(out var details);
        if (logTarget) Log.Info($"唤出前全屏检查 result=0x{monitor:X} {details}");
        if (monitor == _fullscreenMonitor) return;
        Log.Info($"全屏避让变更 old=0x{_fullscreenMonitor:X} new=0x{monitor:X} {details}");
        _fullscreenMonitor = monitor;
        if (DockVisible && _active?.Monitor == monitor) RequestHide();
        UpdateHotspotVisibility(); FullscreenChanged?.Invoke(monitor != IntPtr.Zero);
    }
    private void UpdateHotspotVisibility()
    {
        foreach (var pair in _hotspots)
            if (Paused || (_fullscreenMonitor != IntPtr.Zero && pair.Key == _fullscreenMonitor)) pair.Value.Window.HideWindow();
            else pair.Value.Window.ShowWindowOnly();
    }
    public void RelocateHotspot()
    {
        Log.Info($"热区重排 begin hovered={_hover?.Monitor} dwell={_dwellTimer.IsEnabled}");
        _dwellTimer.Stop(); _hover = null;
        var layouts = _layoutProvider();
        foreach (var key in _hotspots.Keys.Where(k => !layouts.Any(l => l.Monitor == k)).ToArray())
        { _hotspots[key].Window.Dispose(); _hotspots.Remove(key); }
        foreach (var layout in layouts)
        {
            if (!_hotspots.TryGetValue(layout.Monitor, out var pair))
            {
                var window = new NativeHotspotWindow();
                var key = layout.Monitor;
                window.Activated += (_, _) =>
                {
                    if (Paused || !_hotspots.TryGetValue(key, out var current)) return;
                    _dwellTimer.Stop();
                    Log.Info($"热区单击立即唤出 monitor=0x{key:X}");
                    RequestShow(current.Layout, "hotspot-click"); // Re-checks fullscreen for this monitor.
                };
                window.FileDragEntered += (_, _) =>
                {
                    if (Paused || !_hotspots.TryGetValue(key, out var current)) return;
                    _dwellTimer.Stop();
                    Log.Info($"热区 OLE 文件拖入 monitor=0x{key:X}");
                    RequestShow(current.Layout, "ole-drag");
                };
                window.CursorEnter += (_, _) =>
                {
                    Log.Info($"热区进入 monitor=0x{key:X} paused={Paused} fullscreen=0x{_fullscreenMonitor:X}");
                    if (Paused || (_fullscreenMonitor != IntPtr.Zero && key == _fullscreenMonitor) || !_hotspots.TryGetValue(key, out var current)) return;
                    if (!AllowsHoverAtCursor()) { Log.Info("忽略主动收起后的原地热区进入"); return; }
                    _hover = current.Layout; _dwellTimer.Stop(); _dwellTimer.Start();
                };
                window.CursorLeave += (_, _) =>
                {
                    Log.Info($"热区离开 monitor=0x{key:X}");
                    // Hide/show can synthesize WM_MOUSELEAVE. Only a physical exit rearms hover.
                    AllowsHoverAtCursor();
                    if (_hover?.Monitor == key) { _hover = null; _dwellTimer.Stop(); }
                };
                window.Create(layout.X, layout.Y, layout.Width, layout.Height);
                pair = (window, layout);
            }
            else pair.Window.SetRect(layout.X, layout.Y, layout.Width, layout.Height);
            _hotspots[layout.Monitor] = (pair.Window, layout);
        }
        UpdateHotspotVisibility();
        // SetWindowPos can preserve native TrackMouseEvent state. Re-arm once after
        // a layout event when the cursor still hits the hotspot; no resident polling.
        if (!Paused && Win32.GetCursorPos(out var point))
        {
            var hit = Win32.WindowFromPoint(point);
            var hovered = _hotspots.Values.FirstOrDefault(p => p.Window.Handle == hit);
            Log.Info($"热区重排完成 cursor={point.X},{point.Y} hit=0x{hit:X} hotspot={hovered.Layout?.Monitor}");
            if (hovered.Layout is { } layout && (_fullscreenMonitor == IntPtr.Zero || layout.Monitor != _fullscreenMonitor) && AllowsHoverAtCursor())
            {
                _hover = layout; _dwellTimer.Start();
                Log.Info($"热区重排后恢复驻留 monitor=0x{layout.Monitor:X}");
            }
        }
    }
    public void Dispose()
    {
        _dwellTimer.Stop(); _collapseTimer.Stop(); _fullscreenTimer.Stop();
        foreach (var pair in _hotspots.Values) pair.Window.Dispose();
        _hotspots.Clear();
    }
}
