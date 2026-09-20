param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [string]$PreviousSnapshot,
    [string]$LogDirectory
)
# Observation only: never launch, stop, focus, restore or edit the observed app.
$ErrorActionPreference = 'Stop'
$exe = [IO.Path]::GetFullPath($Executable)
$output=[IO.Path]::GetFullPath($OutputPath)
foreach($protectedDirectory in @([IO.Path]::GetDirectoryName($exe),(Join-Path $env:LOCALAPPDATA 'Luma'))) {
    if($output.StartsWith($protectedDirectory.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Observation output must not modify the executable or user configuration directory.'}
}
$previous = $null
if ($PreviousSnapshot -and (Test-Path -LiteralPath $PreviousSnapshot)) {
    $previous = Get-Content -LiteralPath $PreviousSnapshot -Raw -Encoding UTF8 | ConvertFrom-Json
}
$rows = @(Get-CimInstance Win32_Process -OperationTimeoutSec 5)
$roots = @($rows | Where-Object { $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath) -ieq $exe })
if ($roots.Count -gt 1) { throw 'Ambiguous resident instance.' }
$ids = [Collections.Generic.HashSet[int]]::new()
foreach ($r in $roots) { [void]$ids.Add([int]$r.ProcessId) }
do {
    $before = $ids.Count
    foreach ($r in $rows) { if ($ids.Contains([int]$r.ParentProcessId)) { [void]$ids.Add([int]$r.ProcessId) } }
} while ($before -ne $ids.Count)
$at = [DateTimeOffset]::UtcNow
$gap = if ($previous) { ($at - [DateTimeOffset]$previous.atUtc).TotalSeconds } else { $null }
$runtimeIds=[Collections.Generic.HashSet[int]]::new()
foreach($r in $roots){[void]$runtimeIds.Add([int]$r.ProcessId)}
do {
    $before=$runtimeIds.Count
    foreach($r in $rows){if($r.Name -ieq 'msedgewebview2.exe' -and $runtimeIds.Contains([int]$r.ParentProcessId)){[void]$runtimeIds.Add([int]$r.ProcessId)}}
} while($before -ne $runtimeIds.Count)
$processes = @(foreach ($r in $rows) {
    if (-not $ids.Contains([int]$r.ProcessId)) { continue }
    $role = if ($roots.ProcessId -contains $r.ProcessId) { 'host' } elseif ($runtimeIds.Contains([int]$r.ProcessId)) {
        if ($r.CommandLine -match '--type=([^\s]+)') { $Matches[1] } else { 'browser' }
    } else { 'launched-or-helper' }
    $item = [ordered]@{ pid=[int]$r.ProcessId; parentPid=[int]$r.ParentProcessId; name=$r.Name; role=$role; isRuntime=$runtimeIds.Contains([int]$r.ProcessId); createdAtUtc=$r.CreationDate.ToUniversalTime().ToString('o'); privateBytes=$null; workingBytes=$null; handles=$null; cpuSeconds=$null; cpuSingleCorePercent=$null; available=$false }
    try {
        $p = Get-Process -Id $r.ProcessId -ErrorAction Stop
        try {
            # CIM creation timestamps may have less precision than Get-Process.
            if ([Math]::Abs(($p.StartTime.ToUniversalTime() - $r.CreationDate.ToUniversalTime()).TotalMilliseconds) -gt 1) { throw 'Process identity changed.' }
            $item.privateBytes=$p.PrivateMemorySize64; $item.workingBytes=$p.WorkingSet64; $item.handles=$p.HandleCount; $item.cpuSeconds=$p.TotalProcessorTime.TotalSeconds; $item.available=$true
            $prior = @($previous.processes | Where-Object { $_.pid -eq $item.pid -and ([DateTimeOffset]$_.createdAtUtc).UtcDateTime.Ticks -eq $r.CreationDate.ToUniversalTime().Ticks }) | Select-Object -First 1
            if ($prior -and $null -ne $prior.cpuSeconds -and $gap -gt 0 -and $item.cpuSeconds -ge $prior.cpuSeconds) {
                $item.cpuSingleCorePercent=[Math]::Round(($item.cpuSeconds-$prior.cpuSeconds)/$gap*100,4)
            }
        } finally { $p.Dispose() }
    } catch { $item.available=$false }
    [pscustomobject]$item
})
if (-not ('ResidentReadOnly' -as [type])) {
Add-Type -TypeDefinition @'
using System;using System.Collections.Generic;using System.Runtime.InteropServices;using System.Text;
public static class ResidentReadOnly {
 public delegate bool Proc(IntPtr h,IntPtr p);
 [StructLayout(LayoutKind.Sequential)] public struct R {public int L,T,Right,B;}
 [StructLayout(LayoutKind.Sequential)] public struct M {public int Size;public R Monitor,Work;public uint Flags;}
 [DllImport("user32.dll")] static extern bool EnumWindows(Proc p,IntPtr l);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
 [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h,uint m,IntPtr w,IntPtr l,uint f,uint t,out IntPtr r);
 [DllImport("user32.dll")] static extern int GetWindowRgn(IntPtr h,IntPtr r);
 [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int l,int t,int r,int b);
 [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
 [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out R r);
 [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h,uint f);
 [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr h,ref M m);
 [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
 public class Window {public string Kind;public bool Visible,Minimized;public bool? Responsive;public int RegionType;}
 public class Foreground {public uint Pid;public bool? CoversMonitor;}
 public static List<Window> Windows(uint pid){var a=new List<Window>();EnumWindows((h,l)=>{uint p;GetWindowThreadProcessId(h,out p);if(p!=pid)return true;var s=new StringBuilder(256);GetWindowText(h,s,256);var t=s.ToString();if(!t.StartsWith("Luma"))return true;var kind=t=="Luma Dock"?"dock":t=="Luma Hotzone"?"hotzone":t.Contains("\u8bbe\u7f6e")?"settings":t.Contains("\u641c\u7d22")?"search":"other";var region=CreateRectRgn(0,0,0,0);try{IntPtr result;bool? ok=kind=="hotzone"?(bool?)null:SendMessageTimeout(h,0,IntPtr.Zero,IntPtr.Zero,2,500,out result)!=IntPtr.Zero;a.Add(new Window{Kind=kind,Visible=IsWindowVisible(h),Minimized=IsIconic(h),Responsive=ok,RegionType=GetWindowRgn(h,region)});}finally{DeleteObject(region);}return true;},IntPtr.Zero);return a;}
 public static Foreground Front(){var old=SetThreadDpiAwarenessContext(new IntPtr(-4));try{var h=GetForegroundWindow();uint p;GetWindowThreadProcessId(h,out p);R r;var m=new M{Size=Marshal.SizeOf(typeof(M))};bool valid=GetWindowRect(h,out r)&&GetMonitorInfo(MonitorFromWindow(h,2),ref m);return new Foreground{Pid=p,CoversMonitor=valid?(bool?)(r.L<=m.Monitor.L&&r.T<=m.Monitor.T&&r.Right>=m.Monitor.Right&&r.B>=m.Monitor.B):null};}finally{SetThreadDpiAwarenessContext(old);}}
}
'@
}
$windows = @(foreach ($r in $roots) { [ResidentReadOnly]::Windows($r.ProcessId) })
$foreground = [ResidentReadOnly]::Front()
$dwm = $null
try {
    $counters = @(Get-CimInstance Win32_PerfRawData_PerfProc_Process -Filter "Name LIKE 'dwm%'" -OperationTimeoutSec 5 | Where-Object Name -match '^dwm(?:#\d+)?$')
    if ($counters.Count -and @($counters | Where-Object { $null -eq $_.PercentProcessorTime }).Count -eq 0) {
        $dwmAt=[DateTimeOffset]::UtcNow; $seconds=[double](($counters | Measure-Object PercentProcessorTime -Sum).Sum)/1e7
        $dwm=[ordered]@{atUtc=$dwmAt.ToString('o');pidKey=(@($counters.IDProcess | Sort-Object)-join ',');cpuSeconds=$seconds;singleCorePercent=$null;source='Win32_PerfRawData_PerfProc_Process';scope='whole-desktop-not-Luma-attribution'}
        if ($previous.dwm -and $previous.dwm.pidKey -eq $dwm.pidKey -and $seconds -ge $previous.dwm.cpuSeconds) {
            $dt=($dwmAt-[DateTimeOffset]$previous.dwm.atUtc).TotalSeconds
            if ($dt -gt 0) { $dwm.singleCorePercent=[Math]::Round(($seconds-$previous.dwm.cpuSeconds)/$dt*100,4) }
        }
    }
} catch { }
$free=$null
try { $free=[int64](Get-CimInstance Win32_OperatingSystem -OperationTimeoutSec 5).FreePhysicalMemory*1KB } catch { }
$log=$null
if ($LogDirectory -and (Test-Path -LiteralPath $LogDirectory)) {
    $file=Get-ChildItem -LiteralPath $LogDirectory -Filter 'host-*.log' -File | Sort-Object Name -Descending | Select-Object -First 1
    if ($file) {
        $stream=[IO.File]::Open($file.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
        try {
            $end=$stream.Length
            $continued=$roots.Count -eq 1 -and $previous.log -and $previous.rootPid -eq $roots[0].ProcessId -and $previous.log.name -eq $file.Name -and $previous.log.offset -le $end
            $start=if($continued){[int64]$previous.log.offset}else{0L}
            $truncated=$end-$start -gt 65536
            if($truncated){$start=$end-65536}
            [void]$stream.Seek($start,[IO.SeekOrigin]::Begin)
            $bytes=New-Object byte[] ([int]($end-$start));$read=$stream.Read($bytes,0,$bytes.Length)
            $text=[Text.Encoding]::UTF8.GetString($bytes,0,$read)
            $log=[ordered]@{name=$file.Name;offset=($start+$read);deltaComplete=([bool]$continued -and -not $truncated);dockLastLoggedState=if($continued){$previous.log.dockLastLoggedState}else{$null};settingsLastLoggedState=if($continued){$previous.log.settingsLastLoggedState}else{$null};reveals=0;closeRequests=0;launches=0;layoutSyncs=0;stateSource='last-observed-log-message-not-live-IsSuspended'}
            foreach($line in ($text -split "`n")) {
                if($line -match '\u6d6e\u5c9b WebView \u5df2(\u6682\u505c|\u6062\u590d)'){$log.dockLastLoggedState=if($Matches[1] -match '\u6682\u505c'){'suspended'}else{'resumed'}}
                if($line -match '\u7ba1\u7406\u7a97 WebView \u5df2(\u6682\u505c|\u6062\u590d)'){$log.settingsLastLoggedState=if($Matches[1] -match '\u6682\u505c'){'suspended'}else{'resumed'}}
                if($line -match '\u6d6e\u5c9b\u5df2\u786e\u8ba4\u663e\u793a'){$log.reveals++}
                if($line -match '\u539f\u751f\u6d6e\u5c9b\u8bf7\u6c42\u9000\u51fa'){$log.closeRequests++}
                if($line -match '(shell.openItem|folder.open) \u542f\u52a8'){$log.launches++}
                if($line -match 'window.sync \u5e94\u7528'){$log.layoutSyncs++}
            }
        } finally {$stream.Dispose()}
    }
}
$complete=@($processes | Where-Object { -not $_.available }).Count -eq 0
$snapshot=[ordered]@{atUtc=$at.ToString('o');exe=$exe;rootPid=if($roots.Count){[int]$roots[0].ProcessId}else{$null};mode='read-only-resident-observation-not-soak-pass';sampleGapSeconds=$gap;continuousSampleWindow=($null -ne $gap -and $gap -le 62);processes=$processes;tree=[ordered]@{count=$processes.Count;complete=$complete;privateBytes=if($complete){($processes | Measure-Object privateBytes -Sum).Sum}else{$null};handles=if($complete){($processes | Measure-Object handles -Sum).Sum}else{$null}};windows=$windows;foreground=$foreground;systemAvailableBytes=$free;dwm=$dwm;gpuMemoryMeasured=$false}
$snapshot.log=$log
$snapshot.lumaInstanceCount=@($rows | Where-Object Name -ieq 'Luma.exe').Count
$runtime=@($processes | Where-Object isRuntime)
$snapshot.runtime=[ordered]@{count=$runtime.Count;complete=(@($runtime | Where-Object {-not $_.available}).Count -eq 0);privateBytes=($runtime | Measure-Object privateBytes -Sum).Sum;handles=($runtime | Measure-Object handles -Sum).Sum;cpuSingleCorePercent=if(@($runtime | Where-Object {$null -eq $_.cpuSingleCorePercent}).Count -eq 0){($runtime | Measure-Object cpuSingleCorePercent -Sum).Sum}else{$null}}
if(-not $snapshot.runtime.complete){$snapshot.runtime.privateBytes=$null;$snapshot.runtime.handles=$null}
$snapshot | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
