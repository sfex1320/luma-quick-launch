param([Parameter(Mandatory=$true)][long]$WindowHandle,[Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class LumaWindowCapture {
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr w,out Rect r);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr w,IntPtr dc,uint flags);
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr w);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
'@
$previous = [LumaWindowCapture]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
$window = [IntPtr]::new($WindowHandle)
try {
    $rect = [LumaWindowCapture+Rect]::new()
    if (-not [LumaWindowCapture]::GetWindowRect($window,[ref]$rect)) { throw 'Window is unavailable' }
    $bitmap = [System.Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $dc = $graphics.GetHdc()
            try { $printed = [LumaWindowCapture]::PrintWindow($window,$dc,2) }
            finally { $graphics.ReleaseHdc($dc) }
        } finally { $graphics.Dispose() }
        if (-not $printed) { throw 'PrintWindow failed' }
        $bitmap.Save($OutputPath,[System.Drawing.Imaging.ImageFormat]::Png)
        $dpi = [LumaWindowCapture]::GetDpiForWindow($window)
        $color = $bitmap.GetPixel([int]($bitmap.Width/2),[int](16*$dpi/96))
        $titleColors = @{}
        # Text-only caption strip: exclude the app icon and window control buttons.
        for ($y=[int](4*$dpi/96); $y -lt [int](26*$dpi/96); $y++) {
            for ($x=[int](32*$dpi/96); $x -lt [int](150*$dpi/96); $x++) {
                $pixel = $bitmap.GetPixel($x,$y)
                $key = "$($pixel.R),$($pixel.G),$($pixel.B)"
                $titleColors[$key]++
            }
        }
        @{ path=$OutputPath; width=$bitmap.Width; height=$bitmap.Height; dpi=$dpi; captionSample=@($color.R,$color.G,$color.B); titleColors=$titleColors } | ConvertTo-Json -Depth 3 -Compress
    } finally { $bitmap.Dispose() }
} finally { [void][LumaWindowCapture]::SetThreadDpiAwarenessContext($previous) }
