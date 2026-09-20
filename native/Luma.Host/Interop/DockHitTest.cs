namespace Luma.Host.Interop;

internal static class DockHitTest
{
    public static bool Contains(IntPtr hwnd, int screenX, int screenY)
    {
        if (hwnd == IntPtr.Zero || !Win32.GetWindowRect(hwnd, out var bounds)) return false;
        var region = Win32.CreateRectRgn(0, 0, 0, 0);
        if (region == IntPtr.Zero) return false;
        try
        {
            return Win32.GetWindowRgn(hwnd, region) > Win32.NULLREGION &&
                Win32.PtInRegion(region, screenX - bounds.Left, screenY - bounds.Top);
        }
        finally { Win32.DeleteObject(region); }
    }
}
