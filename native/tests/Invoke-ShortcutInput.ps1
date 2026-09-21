param([Parameter(Mandatory=$true)][string]$PayloadBase64)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$payload=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($PayloadBase64))|ConvertFrom-Json
Add-Type -Path (Join-Path $PSScriptRoot 'ShortcutInputNative.cs')
if($payload.op -eq 'safety') { @{fullscreen=[LumaShortcutInput]::Fullscreen();foregroundPid=[LumaShortcutInput]::ForegroundPid()}|ConvertTo-Json -Compress;exit }
if($payload.op -eq 'probe') { @{registered=[LumaShortcutInput]::Probe([uint32]$payload.modifiers,[uint32]$payload.key)}|ConvertTo-Json -Compress;exit }
$p=Get-Process -Id ([int]$payload.pid) -ErrorAction Stop
if($p.Path -ine [IO.Path]::GetFullPath([string]$payload.exe) -or $p.StartTime.ToUniversalTime().Ticks.ToString() -ne [string]$payload.identity){throw 'Process identity mismatch; no action taken'}
$windows=@([LumaShortcutInput]::Inspect([uint32]$p.Id))
if($payload.op -eq 'inspect') { @{windows=$windows;foregroundPid=[LumaShortcutInput]::ForegroundPid();fullscreen=[LumaShortcutInput]::Fullscreen()}|ConvertTo-Json -Depth 5 -Compress;exit }
$window=$windows|Where-Object{$_.hwnd -eq [long]$payload.hwnd}|Select-Object -First 1
if(-not $window){throw 'Target HWND not owned by this process'}
switch($payload.op){
 'focus' {[LumaShortcutInput]::Focus($window.hwnd,[uint32]$p.Id)}
 'click' {[LumaShortcutInput]::Click($window.hwnd,[uint32]$p.Id,[int]$payload.x,[int]$payload.y)}
 'keys' {if([int]$payload.repeats -lt 1 -or [int]$payload.repeats -gt 10){throw 'Invalid repeat count'};[LumaShortcutInput]::Chord($window.hwnd,[uint32]$p.Id,[ushort]$payload.key,[uint32]$payload.modifiers,[int]$payload.repeats)}
 default {throw 'Unsupported input operation'}
}
@{ok=$true;foregroundPid=[LumaShortcutInput]::ForegroundPid()}|ConvertTo-Json -Compress
