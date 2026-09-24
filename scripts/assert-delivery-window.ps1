# Read-only preflight. Never steals focus or terminates an application.
param([switch]$AllowActiveForeground)
$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class LumaDeliveryDesktop {
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
 [StructLayout(LayoutKind.Sequential)] public struct MonitorInfo { public int Size; public Rect Monitor,Work; public int Flags; }
 [StructLayout(LayoutKind.Sequential)] public struct Input { public uint Size,Time; }
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out Rect r);
 [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h,uint flags);
 [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr h,ref MonitorInfo m);
 [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref Input input);
}
'@
$window=[LumaDeliveryDesktop]::GetForegroundWindow()
[uint32]$foregroundPid=0
[void][LumaDeliveryDesktop]::GetWindowThreadProcessId($window,[ref]$foregroundPid)
$foreground=Get-Process -Id $foregroundPid -ErrorAction Stop
$rect=[LumaDeliveryDesktop+Rect]::new()
$monitor=[LumaDeliveryDesktop+MonitorInfo]::new();$monitor.Size=[Runtime.InteropServices.Marshal]::SizeOf($monitor)
if (-not [LumaDeliveryDesktop]::GetWindowRect($window,[ref]$rect) -or -not [LumaDeliveryDesktop]::GetMonitorInfo([LumaDeliveryDesktop]::MonitorFromWindow($window,2),[ref]$monitor)) { throw 'Cannot verify foreground geometry.' }
$fullscreen=$rect.Left -le $monitor.Monitor.Left -and $rect.Top -le $monitor.Monitor.Top -and $rect.Right -ge $monitor.Monitor.Right -and $rect.Bottom -ge $monitor.Monitor.Bottom
$inputState=[LumaDeliveryDesktop+Input]::new();$inputState.Size=[Runtime.InteropServices.Marshal]::SizeOf($inputState)
if (-not [LumaDeliveryDesktop]::GetLastInputInfo([ref]$inputState)) { throw 'Cannot verify user input state.' }
# Windows PowerShell 5.1/.NET Framework has no Environment.TickCount64 property.
# LASTINPUTINFO is 32-bit; use TickCount with unsigned wrap arithmetic in both shells.
$idleMs=([long][Environment]::TickCount - [long]$inputState.Time) -band 0xffffffffL
# Injected input can carry a future timestamp. Unknown freshness must fail closed.
if ($idleMs -gt 0x7fffffffL) { $idleMs=0 }
$result=[ordered]@{atUtc=[DateTime]::UtcNow.ToString('o');foreground=$foreground.ProcessName;foregroundPid=$foregroundPid;fullscreen=$fullscreen;idleSeconds=[math]::Round($idleMs/1000,1)}
$result | ConvertTo-Json -Compress
if ($fullscreen -and $foreground.ProcessName -notin @('explorer')) { throw 'Fullscreen foreground: delivery GUI checks must wait.' }
if (-not $AllowActiveForeground -and $idleMs -lt 120000 -and $foreground.ProcessName -notmatch '^(Codex|Luma|explorer)$') { throw 'User is active in another foreground application: delivery GUI checks must wait.' }
