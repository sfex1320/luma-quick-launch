using System.Windows;
using System.Windows.Interop;
using Luma.Host.Interop;

namespace Luma.Host.Services;

/// <summary>
/// 材质与命中区域：把窗口裁剪到前端上报的真实面板矩形并集（含连接通道），实现
/// 「可交互区域接收鼠标、其余穿透」。承载层禁用整窗 DWM 背景，前端玻璃样式承担材质回退。
/// </summary>
public sealed class BackdropService
{
    /// <summary>相邻面板间允许并入交互通道的最大垂直间隙（DIP）。</summary>
    public const double ChannelGapDip = 240;

    /// <summary>
    /// DWM system backdrop 绘制整个 HWND bounds，空 HRGN 也不能限制它。
    /// 全宽透明 WebView 承载层必须明确 NONE；否则待机仍遮挡半屏。
    /// </summary>
    public void DisableSystemBackdrop(IntPtr hwnd)
    {
        var value = Win32.BackdropNone;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DwmwaSystemBackdropType, ref value, sizeof(int));
    }

    /// <summary>
    /// 应用命中区域：rects 为窗口客户区内 DIP 坐标；转换为物理像素后取圆角矩形并集，
    /// 并把 x 投影重叠、垂直间隙小于阈值的空隙作为连接通道并入。空列表 = 完全穿透。
    /// </summary>
    public void ApplyHitRegion(IntPtr hwnd, IReadOnlyList<Rect> rectsDip, double radiusDip, double dpiScale)
    {
        var scale = dpiScale <= 0 ? 1.0 : dpiScale;
        var rects = rectsDip
            .Where(r => r.Width > 0 && r.Height > 0)
            .Select(r => ShadowBounds(r, scale))
            .ToList();

        if (rects.Count == 0)
        {
            var empty = Win32.CreateRectRgn(0, 0, 0, 0);
            Win32.SetWindowRgn(hwnd, empty, true);
            return;
        }

        // CreateRoundRectRgn 的末两个参数是椭圆直径，不是 CSS 圆角半径。
        var radius = Math.Max(0, (int)Math.Round(2 * radiusDip * scale));
        var channelGap = ChannelGapDip * scale;
        var combined = IntPtr.Zero;
        try
        {
            foreach (var rect in rects)
            {
                var rgn = radius > 0
                    ? Win32.CreateRoundRectRgn((int)rect.X, (int)rect.Y, (int)rect.Right, (int)rect.Bottom, radius, radius)
                    : Win32.CreateRectRgn((int)rect.X, (int)rect.Y, (int)rect.Right, (int)rect.Bottom);
                combined = Combine(combined, rgn);
            }

            foreach (var bridge in ComputeBridges(rects, channelGap))
            {
                var rgn = Win32.CreateRectRgn((int)bridge.X, (int)bridge.Y, (int)bridge.Right, (int)bridge.Bottom);
                combined = Combine(combined, rgn);
            }

            // SetWindowRgn 成功后区域归系统所有，不需要 DeleteObject。
            if (Win32.SetWindowRgn(hwnd, combined, true) == 0)
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            combined = IntPtr.Zero;
        }
        finally
        {
            if (combined != IntPtr.Zero) Win32.DeleteObject(combined);
        }
    }

    /// <summary>把窗口区域恢复为空（完全穿透），用于隐藏/收起时。</summary>
    public void ClearHitRegion(IntPtr hwnd)
    {
        var empty = Win32.CreateRectRgn(0, 0, 0, 0);
        Win32.SetWindowRgn(hwnd, empty, true);
    }

    // A bounded CSS shadow halo, never the full-width carrier window.
    public static Rect ShadowBounds(Rect rect, double scale) => new(
        new Point(Math.Floor((rect.Left - 28) * scale), Math.Floor((rect.Top - 28) * scale)),
        new Point(Math.Ceiling((rect.Right + 28) * scale), Math.Ceiling((rect.Bottom + 36) * scale)));

    private static IntPtr Combine(IntPtr current, IntPtr next)
    {
        if (current == IntPtr.Zero) return next;
        var result = Win32.CreateRectRgn(0, 0, 0, 0);
        if (Win32.CombineRgn(result, current, next, Win32.RGN_OR) == Win32.ERROR)
        {
            Win32.DeleteObject(result);
            Win32.DeleteObject(current);
            Win32.DeleteObject(next);
            return IntPtr.Zero;
        }
        Win32.DeleteObject(current);
        Win32.DeleteObject(next);
        return result;
    }

    /// <summary>主栏与堆叠面板之间的连接通道：x 投影有重叠且垂直间隙不超过阈值的空隙。rects 与 channelGap 均为物理像素。</summary>
    internal static List<Rect> ComputeBridges(IReadOnlyList<Rect> rects, double channelGap)
    {
        var bridges = new List<Rect>();
        for (var i = 0; i < rects.Count; i++)
        {
            for (var j = 0; j < rects.Count; j++)
            {
                if (i == j) continue;
                var top = rects[i];
                var bottom = rects[j];
                if (bottom.Top < top.Bottom) continue; // 只处理上下关系
                var gap = bottom.Top - top.Bottom;
                if (gap <= 0 || gap > channelGap) continue;
                var x1 = Math.Max(top.X, bottom.X);
                var x2 = Math.Min(top.Right, bottom.Right);
                if (x2 - x1 <= 0) continue;
                bridges.Add(new Rect(x1, top.Bottom, x2 - x1, gap));
            }
        }
        return bridges;
    }
}
