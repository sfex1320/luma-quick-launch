using System.Windows;
using Luma.Host.Interop;
using Microsoft.Win32;

namespace Luma.Host.Services;

public sealed class MonitorInfo
{
    public IntPtr Handle { get; init; }
    public Rect PhysicalBounds { get; init; }
    public Rect PhysicalWorkArea { get; init; }
    /// <summary>显示器完整边界（WPF DIP，主屏左上为原点的虚拟桌面坐标）。</summary>
    public Rect Bounds { get; init; }
    /// <summary>任务栏排除后的工作区（DIP）。</summary>
    public Rect WorkArea { get; init; }
    public bool IsPrimary { get; init; }
    /// <summary>该显示器有效 DPI 缩放（96 = 100%）。窗口自身 DPI 由 WPF PerMonitorV2 处理，这里用于区域换算。</summary>
    public double DpiScale { get; init; }
}

/// <summary>显示器枚举与变更通知：DisplaySettingsChanged 时重查并在下一次布局读取新值。</summary>
public sealed class MonitorService : IDisposable
{
    private readonly List<MonitorInfo> _monitors = new();
    private bool _disposed;

    public event Action? DisplaysChanged;

    public MonitorService()
    {
        Refresh();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public IReadOnlyList<MonitorInfo> Monitors
    {
        get { lock (this) return _monitors.Count > 0 ? _monitors.ToList() : new List<MonitorInfo> { Primary }; }
    }

    public MonitorInfo Primary
    {
        get
        {
            lock (this) return _monitors.FirstOrDefault(m => m.IsPrimary) ?? new MonitorInfo
            {
                PhysicalBounds = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight),
                PhysicalWorkArea = SystemParameters.WorkArea,
                Bounds = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight),
                WorkArea = new Rect(SystemParameters.WorkArea.X, SystemParameters.WorkArea.Y, SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height),
                IsPrimary = true,
                DpiScale = 1.0,
            };
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Log.Info("检测到显示设置变化，重新枚举显示器");
        Refresh();
        DisplaysChanged?.Invoke();
    }

    private void Refresh()
    {
        var list = new List<MonitorInfo>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, ref rect, _) =>
        {
            var info = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
            if (Win32.GetMonitorInfo(monitor, ref info))
            {
                double scale = 1.0;
                if (Win32.GetDpiForMonitor(monitor, Win32.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
                    scale = dpiX / 96.0;
                list.Add(new MonitorInfo
                {
                    Handle = monitor,
                    PhysicalBounds = ToDip(info.rcMonitor, 1),
                    PhysicalWorkArea = ToDip(info.rcWork, 1),
                    Bounds = ToDip(info.rcMonitor, scale),
                    WorkArea = ToDip(info.rcWork, scale),
                    IsPrimary = (info.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0,
                    DpiScale = scale,
                });
            }
            return true;
        }, IntPtr.Zero);

        if (list.Count == 0)
        {
            Log.Warn("显示器枚举为空，回退 WPF 主屏参数");
            return;
        }
        lock (this)
        {
            _monitors.Clear();
            _monitors.AddRange(list);
        }
        foreach (var monitor in list)
            Log.Info($"显示器 bounds={monitor.Bounds} work={monitor.WorkArea} primary={monitor.IsPrimary} dpi={monitor.DpiScale:0.##}");
    }

    private static Rect ToDip(Win32.RECT rect, double scale)
    {
        if (scale <= 0) scale = 1.0;
        return new Rect(rect.Left / scale, rect.Top / scale, (rect.Right - rect.Left) / scale, (rect.Bottom - rect.Top) / scale);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }
}
