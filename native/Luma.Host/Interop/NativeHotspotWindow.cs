using System.Runtime.InteropServices;
using Luma.Host.Services;

namespace Luma.Host.Interop;

/// <summary>
/// 纯 Win32 顶边热区窗口：WS_POPUP + WS_EX_LAYERED(SLWA alpha=1) + WS_EX_NOACTIVATE。
/// 用 SetLayeredWindowAttributes 的整窗 alpha（非 per-pixel），hit test 完全基于窗口矩形，
/// 行为稳定可控，不受 WPF AllowsTransparency 布局/DPI 怪癖影响。
/// 消息由宿主 UI 线程（WPF Dispatcher 泵）分发；WM_MOUSEMOVE/WM_MOUSELEAVE 驱动驻留检测。
/// 所有坐标为物理像素。
/// </summary>
internal sealed class NativeHotspotWindow : IDisposable
{
    private const string ClassName = "LumaHotzoneWnd";
    private const int WM_DESTROY = 0x0002;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    private static WndProcDelegate? _staticProc;
    private static readonly Dictionary<IntPtr, NativeHotspotWindow> Windows = new();

    private IntPtr _hwnd;
    private bool _trackingLeave;
    private bool _leftPressStartedHere;
    private bool _disposed;
    private OleDropRegistration? _dropRegistration;
    private readonly HoverMovementGate _hoverMovement = new();
    internal bool CurrentMoveCanStartHover { get; private set; }

    public event EventHandler? Activated;
    public event EventHandler? CursorEnter;
    public event EventHandler? CursorMoved;
    public event EventHandler? CursorLeave;
    public event EventHandler? FileDragEntered;
    public IntPtr Handle => _hwnd;
    public bool IsCreated => _hwnd != IntPtr.Zero;

    public NativeHotspotWindow()
    {
        RegisterClassOnce();
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        NativeHotspotWindow? owner;
        lock (Windows) Windows.TryGetValue(hwnd, out owner);
        try
        {
            return owner is not null ? owner.InstanceWndProc(hwnd, msg, wParam, lParam) : DefWindowProc(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            // Preserve evidence before an exception crosses the reverse P/Invoke boundary.
            // Do not continue dispatching with partially-mutated activation state.
            Log.Error($"热区窗口回调异常 pid={Environment.ProcessId} hwnd=0x{hwnd:X} message=0x{msg:X} {ex}");
            throw;
        }
    }

    public void Create(int x, int y, int width, int height)
    {
        if (_hwnd != IntPtr.Zero) return;
        ResetHoverMovement();
        _hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
            ClassName, "Luma Hotzone",
            WS_POPUP,
            x, y, width, height,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            Log.Error($"热区窗口创建失败 GetLastError={Marshal.GetLastWin32Error()}");
            return;
        }
        lock (Windows) Windows[_hwnd] = this;
        try
        {
            _dropRegistration = new OleDropRegistration(_hwnd,
                new OleFileDropTarget(() => FileDragEntered?.Invoke(this, EventArgs.Empty)));
            Log.Info($"热区 OLE 注册完成 hwnd=0x{_hwnd:X}");
        }
        catch (Exception ex) { Log.Error($"热区 OLE 注册失败: {ex.Message}"); }
        SetLayeredWindowAttributes(_hwnd, 0, 1, LWA_ALPHA);
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        Log.Info($"热区窗口已创建 hwnd=0x{_hwnd:X} rect=({x},{y},{width}x{height})");
    }

    public void SetRect(int x, int y, int width, int height)
    {
        ResetHoverMovement();
        _leftPressStartedHere = false;
        if (_hwnd == IntPtr.Zero) return;
        var positioned = SetWindowPos(_hwnd, HwndTopmost, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        Log.Info($"热区定位 hwnd=0x{_hwnd:X} rect={x},{y},{width},{height} tracking={_trackingLeave} ok={positioned}");
    }

    public void HideWindow()
    {
        ResetHoverMovement();
        _trackingLeave = false;
        _leftPressStartedHere = false;
        if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SW_HIDE);
    }

    public void ShowWindowOnly()
    {
        ResetHoverMovement();
        if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
    }

    private void ResetHoverMovement()
    {
        var available = Win32.GetCursorPos(out var point);
        _hoverMovement.Reset(available, point.X, point.Y);
        CurrentMoveCanStartHover = false;
    }

    private IntPtr InstanceWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case 0x0201: // WM_LBUTTONDOWN: a release alone can come from another app's drag.
                _leftPressStartedHere = true;
                break;
            case 0x0202: // WM_LBUTTONUP: clicking the top-edge shortcut opens immediately.
                var clickedHere = _leftPressStartedHere;
                _leftPressStartedHere = false;
                if (!clickedHere) break;
                Log.Info($"热区原生单击 hwnd=0x{hwnd:X} {ActivationDiagnostics.Capture(hwnd)}");
                Activated?.Invoke(this, EventArgs.Empty);
                break;
            case 0x001F: // WM_CANCELMODE.
            case 0x0215: // WM_CAPTURECHANGED.
                _leftPressStartedHere = false;
                break;
            case Win32.WM_MOUSEMOVE:
                var positionAvailable = Win32.GetCursorPos(out var position);
                var sourceAvailable = ActivationDiagnostics.ReadMouseMessageSource(out var device, out var origin);
                CurrentMoveCanStartHover = _hoverMovement.Observe(positionAvailable, position.X, position.Y,
                    sourceAvailable, device, origin);
                if (!_trackingLeave)
                {
                    var track = new Win32.TRACKMOUSEEVENT
                    {
                        cbSize = Marshal.SizeOf<Win32.TRACKMOUSEEVENT>(),
                        dwFlags = Win32.TME_LEAVE,
                        hwndTrack = hwnd,
                    };
                    Win32.TrackMouseEvent(ref track);
                    _trackingLeave = true;
                    var packed = lParam.ToInt64();
                    Log.Info($"热区原生 WM_MOUSEMOVE enter hwnd=0x{hwnd:X} client={unchecked((short)(packed & 0xffff))},{unchecked((short)((packed >> 16) & 0xffff))} hoverStartEligible={CurrentMoveCanStartHover} {ActivationDiagnostics.Capture(hwnd)}");
                    CursorEnter?.Invoke(this, EventArgs.Empty);
                }
                CursorMoved?.Invoke(this, EventArgs.Empty);
                break;
            case Win32.WM_MOUSELEAVE:
                ResetHoverMovement();
                _trackingLeave = false;
                _leftPressStartedHere = false;
                Log.Info($"热区原生 WM_MOUSELEAVE hwnd=0x{hwnd:X}");
                CursorLeave?.Invoke(this, EventArgs.Empty);
                break;
            case WM_DESTROY:
                _dropRegistration?.Dispose(); _dropRegistration = null;
                lock (Windows) Windows.Remove(hwnd);
                _hwnd = IntPtr.Zero;
                break;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dropRegistration?.Dispose(); _dropRegistration = null;
        if (_hwnd != IntPtr.Zero)
        {
            lock (Windows) Windows.Remove(_hwnd);
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private void RegisterClassOnce()
    {
        if (_staticProc is not null) return;
        _staticProc = StaticWndProc;
        var classNamePtr = Marshal.StringToHGlobalUni(ClassName);
        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_staticProc),
            lpszClassName = classNamePtr,
            hInstance = GetModuleHandle(null),
        };
        var atom = RegisterClass(ref wc);
        var err = Marshal.GetLastWin32Error();
        Marshal.FreeHGlobal(classNamePtr);
        if (atom == 0 && err != 1410) // ERROR_CLASS_ALREADY_EXISTS
            Log.Error($"热区窗口类注册失败 atom={atom} err={err}");
        else
            Log.Info($"热区窗口类注册完成 atom={atom} err={err}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private const int WS_POPUP = unchecked((int)0x8000_0000);
    private const int WS_EX_LAYERED = 0x0008_0000;
    private const int WS_EX_NOACTIVATE = 0x0800_0000;
    private const int WS_EX_TOOLWINDOW = 0x0000_0080;
    private const int WS_EX_TOPMOST = 0x0000_0008;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint LWA_ALPHA = 2;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
}
