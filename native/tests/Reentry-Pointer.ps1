$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot 'ShortcutInputNative.cs')
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class ReentryPointer {
 [StructLayout(LayoutKind.Sequential)] public struct Point { public int X,Y; }
 [DllImport("user32.dll")] static extern bool GetCursorPos(out Point p);
 [DllImport("user32.dll")] static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
 public static Point Read() { Point p; if(!GetCursorPos(out p)) throw new Exception("Cursor unavailable"); return p; }
 public static void Move(int x,int y) { if(!SetCursorPos(x,y)) throw new Exception("Move failed"); }
}
'@
[void][ReentryPointer]::SetThreadDpiAwarenessContext([IntPtr](-4))
$original = [ReentryPointer]::Read()
$last = $original
$foregroundOwner = [LumaShortcutInput]::ForegroundPid()
[Console]::WriteLine('ready')
try {
 for ($n=0; $n -lt 40; $n++) {
  $line = [Console]::ReadLine()
  if ($null -eq $line -or $line -eq 'quit') { break }
  $current = [ReentryPointer]::Read()
  if ($current.X -ne $last.X -or $current.Y -ne $last.Y) { throw 'Physical user pointer moved; stopping without further input.' }
  if ([LumaShortcutInput]::Fullscreen() -or [LumaShortcutInput]::ForegroundPid() -ne $foregroundOwner) { throw 'Foreground changed; stopping without refocusing.' }
  $xy = $line.Split(',')
  [ReentryPointer]::Move([int]$xy[0],[int]$xy[1])
  $last = [ReentryPointer]::Read()
  [Console]::WriteLine('moved')
 }
} finally {
 $current = [ReentryPointer]::Read()
 if ($current.X -eq $last.X -and $current.Y -eq $last.Y) { [ReentryPointer]::Move($original.X,$original.Y) }
}
