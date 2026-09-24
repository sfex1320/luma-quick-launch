using System.Runtime.InteropServices;
using System.Text;
using Luma.Host.Interop;

namespace Luma.Host.Services;

/// <summary>Event-only diagnostics. No polling, input hooks, window titles or activation side effects.</summary>
internal static class ActivationDiagnostics
{
    internal static bool ReadMouseMessageSource(out int device, out int origin)
    {
        var available = GetCurrentInputMessageSource(out var source);
        device = source.DeviceType;
        origin = source.OriginId;
        return available;
    }

    internal static string Capture(IntPtr hotspot = default)
    {
        try
        {
            var available = Win32.GetCursorPos(out var point);
            var physicalAvailable = GetPhysicalCursorPos(out var physical);
            var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
            var cursorAvailable = GetCursorInfo(ref cursor);
            var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            var inputAvailable = GetLastInputInfo(ref input);
            var sourceAvailable = GetCurrentInputMessageSource(out var source);
            var hit = available ? Win32.WindowFromPoint(point) : IntPtr.Zero;
            var rect = new Win32.RECT();
            var rectAvailable = hotspot != IntPtr.Zero && Win32.GetWindowRect(hotspot, out rect);
            var foreground = Win32.GetForegroundWindow();
            return $"cursor={(available ? $"{point.X},{point.Y}" : "unavailable")} " +
                $"physical={(physicalAvailable ? $"{physical.X},{physical.Y}" : "unavailable")} " +
                $"cursorFlags={(cursorAvailable ? cursor.Flags.ToString() : "unavailable")} " +
                $"lastInputTick={(inputAvailable ? input.Time.ToString() : "unavailable")} " +
                $"inputAgeMs={(inputAvailable ? Age(unchecked((uint)Environment.TickCount64), input.Time).ToString() : "unavailable")} " +
                $"messageAgeMs={Age(unchecked((uint)Environment.TickCount64), unchecked((uint)GetMessageTime()))} " +
                $"inputSource={(sourceAvailable ? $"{source.DeviceType}/{source.OriginId}" : "unavailable")} " +
                $"hit=0x{hit:X}:{ClassOf(hit)} hotspot=0x{hotspot:X} " +
                $"actualRect={(rectAvailable ? $"{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}" : "unavailable")} " +
                $"foreground=0x{foreground:X}:{ClassOf(foreground)} dpiContext=0x{GetThreadDpiAwarenessContext():X}";
        }
        catch (Exception ex) { return $"diagnosticUnavailable={ex.GetType().Name}"; }
    }

    // Win32 input/message ticks are unsigned 32-bit values, including across wraparound.
    internal static uint Age(uint now, uint then) => unchecked(now - then);
    private static string ClassOf(IntPtr window)
    {
        if (window == IntPtr.Zero) return "none";
        var name = new StringBuilder(128);
        Win32.GetClassName(window, name, name.Capacity);
        return name.ToString();
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo { public int Size, Flags; public IntPtr Handle; public Win32.POINT Position; }
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size, Time; }
    [StructLayout(LayoutKind.Sequential)]
    private struct InputMessageSource { public int DeviceType, OriginId; }
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out Win32.POINT point);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo cursor);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo input);
    [DllImport("user32.dll")] private static extern bool GetCurrentInputMessageSource(out InputMessageSource source);
    [DllImport("user32.dll")] private static extern int GetMessageTime();
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
}
