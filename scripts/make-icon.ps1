$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$outDir = Join-Path $PSScriptRoot '..\native\Luma.Host\Assets'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outPath = Join-Path $outDir 'luma.ico'

function New-Bitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    # 水晶蓝渐变圆角底
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect,
        [System.Drawing.Color]::FromArgb(255, 122, 232, 212),
        [System.Drawing.Color]::FromArgb(255, 74, 144, 255), 60)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = [int]($size * 0.28)
    $path.AddArc(1, 1, 2*$r, 2*$r, 180, 90)
    $path.AddArc($size-1-2*$r, 1, 2*$r, 2*$r, 270, 90)
    $path.AddArc($size-1-2*$r, $size-1-2*$r, 2*$r, 2*$r, 0, 90)
    $path.AddArc(1, $size-1-2*$r, 2*$r, 2*$r, 90, 90)
    $path.CloseFigure()
    $g.FillPath($brush, $path)
    # 中心白色圆点（「项目」的抽象）
    $cx = $size * 0.5; $cy = $size * 0.52; $rad = $size * 0.2
    $dot = New-Object System.Drawing.Drawing2D.GraphicsPath
    $dot.AddEllipse([float]($cx-$rad), [float]($cy-$rad), [float](2*$rad), [float](2*$rad))
    $g.FillPath([System.Drawing.Brushes]::White, $dot)
    $g.Dispose()
    return $bmp
}

# ICO：256/48/32/16 四档 PNG 压缩条目（256 为 PNG，小尺寸 BMP 也可，统一 PNG 简化）
$sizes = @(256, 48, 32, 16)
$streams = @()
foreach ($s in $sizes) {
    $bmp = New-Bitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $ms.Position = 0
    $streams += ,@($s, $ms)
}

$fs = [System.IO.File]::Create($outPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0)      # reserved
$bw.Write([uint16]1)      # type: icon
$bw.Write([uint16]$streams.Count)
$offset = 6 + 16 * $streams.Count
foreach ($entry in $streams) {
    $s = $entry[0]; $ms = $entry[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim)   # width
    $bw.Write([byte]$dim)   # height
    $bw.Write([byte]0)      # palette
    $bw.Write([byte]0)      # reserved
    $bw.Write([uint16]1)    # color planes
    $bw.Write([uint16]32)   # bits per pixel
    $bw.Write([uint32]$ms.Length)
    $bw.Write([uint32]$offset)
    $offset += $ms.Length
}
foreach ($entry in $streams) {
    $ms = $entry[1]
    $bytes = $ms.ToArray()
    $bw.Write($bytes)
    $ms.Dispose()
}
$bw.Flush(); $bw.Close()
Write-Host "icon written: $outPath ($((Get-Item $outPath).Length) bytes)"
