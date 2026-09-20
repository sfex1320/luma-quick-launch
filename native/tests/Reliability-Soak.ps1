param(
    [int]$Seconds = 18000,
    [int]$SampleIntervalSeconds = 30,
    [int]$RevealEverySeconds = 300,
    [int64]$MaxPrivateBytes = 2GB,
    [int64]$MaxGrowthBytes = 1GB,
    [int]$MaxHandles = 20000,
    [int64]$MinSystemAvailableBytes = 1GB,
    [double]$MaxSustainedCpuSingleCorePercent = 200,
    [double]$MaxSustainedDwmSingleCorePercent = 50,
    [switch]$InjectRendererFailure,
    [switch]$UseUserState
)
$ErrorActionPreference = 'Stop'
if ($Seconds -lt 60) { throw 'Seconds must be at least 60.' }
if ($SampleIntervalSeconds -lt 2) { throw 'SampleIntervalSeconds must be at least 2.' }
$exe = $env:LUMA_TEST_EXE
if ([string]::IsNullOrWhiteSpace($exe) -or -not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw 'Set LUMA_TEST_EXE to the exact built or published Luma.exe to test.'
}
$exe = (Resolve-Path -LiteralPath $exe).Path
$guard = Join-Path $PSScriptRoot 'assert-executable-idle.mjs'
& node -e "const {pathToFileURL}=require('url');import(pathToFileURL(process.argv[1])).then(m=>m.assertExecutableIdle(process.argv[2])).catch(e=>{console.error(e.message);process.exit(1)})" $guard $exe
if ($LASTEXITCODE -ne 0) { throw 'The exact test executable is already running or could not be inspected; soak refused.' }
$id = [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "luma-reliability-soak-$id"
$dataDirectory = Join-Path $testRoot 'data'
$samplesFile = Join-Path $testRoot 'samples.csv'
$resultFile = Join-Path $testRoot 'result.json'
New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
Write-Host "SOAK_EVIDENCE=$resultFile"
$userState = Join-Path $env:LOCALAPPDATA 'Luma\state.json'
function Hash-OrAbsent([string]$Path) { if (Test-Path -LiteralPath $Path) { (Get-FileHash -LiteralPath $Path).Hash } else { '<absent>' } }
$userStateBefore = Hash-OrAbsent $userState
if ($UseUserState -and (Test-Path -LiteralPath $userState -PathType Leaf)) {
    # Parse before copying. Only saved references/preferences are copied; WebView cache and logs remain isolated.
    Get-Content -LiteralPath $userState -Raw | ConvertFrom-Json | Out-Null
    Copy-Item -LiteralPath $userState -Destination (Join-Path $dataDirectory 'state.json')
}
$oldDataDirectory = $env:LUMA_DATA_DIRECTORY
$oldTestSession = $env:LUMA_TEST_SESSION
$rootProcess = $null
$watchdogReason = $null
$faultInjected = $false

function Get-DwmCpuSeconds {
    # Protected DWM processes may deny Get-Process.CPU to ordinary users. The
    # read-only Windows process performance provider exposes cumulative 100ns
    # CPU ticks without elevating the application or changing system settings.
    try {
        $counters = @(Get-CimInstance Win32_PerfRawData_PerfProc_Process -Filter "Name LIKE 'dwm%'" -OperationTimeoutSec 5 |
            Where-Object { $_.Name -match '^dwm(?:#\d+)?$' })
        if ($counters.Count -gt 0 -and @($counters | Where-Object { $null -eq $_.PercentProcessorTime }).Count -eq 0) {
            return [double](($counters | Measure-Object -Property PercentProcessorTime -Sum).Sum) / 10000000.0
        }
    } catch { }
    return $null
}

function Get-TreeSnapshot([int]$RootId) {
    $rows = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, Name, CommandLine)
    $ids = [Collections.Generic.HashSet[int]]::new()
    [void]$ids.Add($RootId)
    do {
        $before = $ids.Count
        foreach ($row in $rows) { if ($ids.Contains([int]$row.ParentProcessId)) { [void]$ids.Add([int]$row.ProcessId) } }
    } while ($ids.Count -ne $before)
    $cpu = 0.0; $private = 0L; $working = 0L; $handles = 0; $live = 0
    foreach ($processId in $ids) {
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($process) {
            $cpu += $process.TotalProcessorTime.TotalSeconds
            $private += $process.PrivateMemorySize64
            $working += $process.WorkingSet64
            $handles += $process.HandleCount
            $live++
        }
    }
    $dwmCpu = Get-DwmCpuSeconds
    $os = Get-CimInstance Win32_OperatingSystem | Select-Object -First 1
    [pscustomobject]@{ At = [DateTimeOffset]::UtcNow; ProcessCount = $live; PrivateBytes = $private; WorkingBytes = $working; Handles = $handles; CpuSeconds = $cpu; DwmCpuSeconds = $dwmCpu; FreePhysicalBytes = [int64]$os.FreePhysicalMemory * 1KB; Rows = $rows; Ids = $ids }
}

try {
    $baselineStarted = [DateTimeOffset]::UtcNow
    $baselineDwmStart = Get-DwmCpuSeconds
    Start-Sleep -Seconds 5
    $baselineSeconds = ([DateTimeOffset]::UtcNow - $baselineStarted).TotalSeconds
    $baselineDwmEnd = Get-DwmCpuSeconds
    $baselineDwmPercent = if ($null -eq $baselineDwmStart -or $null -eq $baselineDwmEnd) { $null } else { ($baselineDwmEnd - $baselineDwmStart) / $baselineSeconds * 100 }
    $env:LUMA_DATA_DIRECTORY = $dataDirectory
    $env:LUMA_TEST_SESSION = 'soak'
    Add-Type -TypeDefinition 'using System; using System.Text; using System.Runtime.InteropServices; public static class LumaSoakWindow { [StructLayout(LayoutKind.Sequential)] struct RECT { public int L,T,R,B; } public delegate bool EnumProc(IntPtr h, IntPtr l); [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l); [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h); [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out RECT r); [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p); [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder b, int n); [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l); [DllImport("user32.dll", SetLastError=true)] static extern IntPtr SendMessageTimeout(IntPtr h,uint m,IntPtr w,IntPtr l,uint f,uint t,out IntPtr r); public static bool Minimize(IntPtr h) { return PostMessage(h,0x112,new IntPtr(0xF020),IntPtr.Zero); } public static bool Responsive(IntPtr h) { IntPtr r; return SendMessageTimeout(h,0,IntPtr.Zero,IntPtr.Zero,2,750,out r)!=IntPtr.Zero; } public static IntPtr FindSettings(int pid) { IntPtr found=IntPtr.Zero; EnumWindows((h,l)=>{ uint p; RECT r; GetWindowThreadProcessId(h,out p); if(p!=(uint)pid || !IsWindowVisible(h) || !GetWindowRect(h,out r) || r.R-r.L<480 || r.B-r.T<360)return true; var b=new StringBuilder(256); GetWindowText(h,b,b.Capacity); var t=b.ToString(); if(t.Length>0 && t!="Luma Dock") { found=h; return false; } return true; },IntPtr.Zero); return found; } }'
    $rootProcess = Start-Process -FilePath $exe -ArgumentList '--settings' -PassThru -WindowStyle Hidden
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 250
        $settingsHandle = [LumaSoakWindow]::FindSettings($rootProcess.Id)
    } until ($rootProcess.HasExited -or $settingsHandle -ne 0 -or [DateTimeOffset]::UtcNow -ge $deadline)
    if ($rootProcess.HasExited) { throw "Isolated Luma exited early with code $($rootProcess.ExitCode)." }
    if ($settingsHandle -eq 0) { throw 'Settings window did not become available for minimization.' }
    if (-not [LumaSoakWindow]::Minimize($settingsHandle)) { throw 'Could not minimize the isolated settings window.' }
    Start-Sleep -Seconds 5
    $first = Get-TreeSnapshot $rootProcess.Id
    $previous = $first
    $started = [DateTimeOffset]::UtcNow
    $nextReveal = $started.AddSeconds($RevealEverySeconds)
    $faultAt = $started.AddSeconds([Math]::Max(30, [Math]::Min(300, [int]($Seconds / 2))))
    $highCpuSamples = 0
    $highDwmSamples = 0
    $unresponsiveSamples = 0
    $sampleCount = 0
    $minPrivate = $first.PrivateBytes; $maxPrivate = $first.PrivateBytes
    $minHandles = $first.Handles; $maxHandles = $first.Handles
    'utc,elapsedSeconds,processCount,privateMB,workingMB,handles,cpuSingleCorePercent,dwmSingleCorePercent' | Set-Content -LiteralPath $samplesFile -Encoding UTF8
    while (([DateTimeOffset]::UtcNow - $started).TotalSeconds -lt $Seconds) {
        Start-Sleep -Seconds $SampleIntervalSeconds
        if ($rootProcess.HasExited) { $watchdogReason = 'host-exited'; break }
        $now = [DateTimeOffset]::UtcNow
        if ($RevealEverySeconds -gt 0 -and $now -ge $nextReveal) {
            $signal = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden
            $signal.WaitForExit(5000) | Out-Null
            $nextReveal = $now.AddSeconds($RevealEverySeconds)
        }
        $current = Get-TreeSnapshot $rootProcess.Id
        if ($InjectRendererFailure -and -not $faultInjected -and $now -ge $faultAt) {
            $renderer = $current.Rows | Where-Object { $current.Ids.Contains([int]$_.ProcessId) -and $_.Name -eq 'msedgewebview2.exe' -and $_.CommandLine -match '--type=renderer' } | Select-Object -First 1
            if ($renderer) { Stop-Process -Id ([int]$renderer.ProcessId) -Force; $faultInjected = $true }
        }
        $wall = [Math]::Max(0.001, ($current.At - $previous.At).TotalSeconds)
        if ($wall -gt ($SampleIntervalSeconds * 2 + 2)) { $watchdogReason = 'measurement-gap-or-system-sleep'; break }
        $cpuPercent = ($current.CpuSeconds - $previous.CpuSeconds) / $wall * 100
        $dwmPercent = if ($null -eq $current.DwmCpuSeconds -or $null -eq $previous.DwmCpuSeconds) { $null } else { ($current.DwmCpuSeconds - $previous.DwmCpuSeconds) / $wall * 100 }
        $sampleCount++
        $minPrivate = [Math]::Min($minPrivate, $current.PrivateBytes); $maxPrivate = [Math]::Max($maxPrivate, $current.PrivateBytes)
        $minHandles = [Math]::Min($minHandles, $current.Handles); $maxHandles = [Math]::Max($maxHandles, $current.Handles)
        '{0},{1:F1},{2},{3:F1},{4:F1},{5},{6:F3},{7:F3}' -f $current.At.ToString('O'), ($current.At - $started).TotalSeconds, $current.ProcessCount, ($current.PrivateBytes / 1MB), ($current.WorkingBytes / 1MB), $current.Handles, $cpuPercent, $dwmPercent | Add-Content -LiteralPath $samplesFile -Encoding UTF8
        if ($current.PrivateBytes -gt $MaxPrivateBytes) { $watchdogReason = 'private-memory-limit'; break }
        if (($current.PrivateBytes - $first.PrivateBytes) -gt $MaxGrowthBytes) { $watchdogReason = 'private-memory-growth-limit'; break }
        if ($current.Handles -gt $MaxHandles) { $watchdogReason = 'handle-limit'; break }
        if ($current.FreePhysicalBytes -lt $MinSystemAvailableBytes) { $watchdogReason = 'system-memory-pressure'; break }
        if ($cpuPercent -gt $MaxSustainedCpuSingleCorePercent) { $highCpuSamples++ } else { $highCpuSamples = 0 }
        if ($highCpuSamples -ge 3) { $watchdogReason = 'sustained-cpu-limit'; break }
        if ($null -ne $dwmPercent -and $dwmPercent -gt $MaxSustainedDwmSingleCorePercent) { $highDwmSamples++ } else { $highDwmSamples = 0 }
        if ($highDwmSamples -ge 3) { $watchdogReason = 'sustained-dwm-cpu-limit'; break }
        if ([LumaSoakWindow]::Responsive($settingsHandle)) { $unresponsiveSamples = 0 } else { $unresponsiveSamples++ }
        if ($unresponsiveSamples -ge 3) { $watchdogReason = 'host-window-unresponsive'; break }
        $previous = $current
    }
    $final = Get-TreeSnapshot $rootProcess.Id
    $logLines = @(Get-ChildItem -LiteralPath (Join-Path $dataDirectory 'logs') -Filter 'host-*.log' -ErrorAction SilentlyContinue |
        ForEach-Object { Get-Content -LiteralPath $_.FullName })
    $dockLabel = -join [char[]](0x6d6e, 0x5c9b)
    $settingsLabel = -join [char[]](0x7ba1, 0x7406, 0x7a97)
    $suspendedLabel = -join [char[]](0x5df2, 0x6682, 0x505c)
    $failureIndex = -1
    for ($lineIndex = 0; $lineIndex -lt $logLines.Count; $lineIndex++) { if ($logLines[$lineIndex] -match 'RenderProcessExited action=Reload') { $failureIndex = $lineIndex; break } }
    $revealAfterFailure = $failureIndex -ge 0 -and [bool](@($logLines[($failureIndex + 1)..($logLines.Count - 1)] -match 'phase=AwaitingExpanded expanded=True').Count)
    $layoutAfterFailure = $failureIndex -ge 0 -and [bool](@($logLines[($failureIndex + 1)..($logLines.Count - 1)] -match 'phase=Hidden expanded=False').Count)
    $result = [ordered]@{
        result = if ($watchdogReason) { 'watchdog-stopped' } else { 'completed' }
        reason = $watchdogReason
        exe = $exe
        dataDirectory = $dataDirectory
        rootPid = $rootProcess.Id
        dwmCpuSource = 'Win32_PerfRawData_PerfProc_Process cumulative 100ns counter; null when unavailable'
        gpuMemoryMeasured = $false
        requestedSeconds = $Seconds
        elapsedSeconds = [Math]::Round(([DateTimeOffset]::UtcNow - $started).TotalSeconds, 1)
        initialPrivateMB = [Math]::Round($first.PrivateBytes / 1MB, 1)
        finalPrivateMB = [Math]::Round($final.PrivateBytes / 1MB, 1)
        privateGrowthMB = [Math]::Round(($final.PrivateBytes - $first.PrivateBytes) / 1MB, 1)
        initialHandles = $first.Handles
        finalHandles = $final.Handles
        sampleCount = $sampleCount
        minPrivateMB = [Math]::Round($minPrivate / 1MB, 1)
        maxPrivateMB = [Math]::Round($maxPrivate / 1MB, 1)
        minHandles = $minHandles
        maxHandlesObserved = $maxHandles
        dwmBaselineSingleCorePercent = if ($null -eq $baselineDwmPercent) { $null } else { [Math]::Round($baselineDwmPercent, 3) }
        dwmRunSingleCorePercent = if ($null -eq $first.DwmCpuSeconds -or $null -eq $final.DwmCpuSeconds) { $null } else { [Math]::Round(($final.DwmCpuSeconds - $first.DwmCpuSeconds) / [Math]::Max(0.001, ($final.At - $first.At).TotalSeconds) * 100, 3) }
        dwmDeltaSingleCorePercent = if ($null -eq $baselineDwmPercent -or $null -eq $first.DwmCpuSeconds -or $null -eq $final.DwmCpuSeconds) { $null } else { [Math]::Round((($final.DwmCpuSeconds - $first.DwmCpuSeconds) / [Math]::Max(0.001, ($final.At - $first.At).TotalSeconds) * 100) - $baselineDwmPercent, 3) }
        faultRequested = [bool]$InjectRendererFailure
        faultInjected = $faultInjected
        dockSuspendLogged = [bool]($logLines -match ([regex]::Escape("$dockLabel WebView $suspendedLabel")))
        settingsSuspendLogged = [bool]($logLines -match ([regex]::Escape("$settingsLabel WebView $suspendedLabel")))
        rendererFailureLogged = [bool]($logLines -match 'RenderProcessExited action=Reload')
        revealAfterRendererRecoveryLogged = $revealAfterFailure
        rendererRecoveryLayoutReadyLogged = $layoutAfterFailure
        samples = $samplesFile
        userStateUnchanged = ((Hash-OrAbsent $userState) -eq $userStateBefore)
    }
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resultFile -Encoding UTF8
    $result | ConvertTo-Json -Depth 5
    if ($watchdogReason) { exit 2 }
}
finally {
    if ($rootProcess -and -not $rootProcess.HasExited) {
        # --shutdown is EXE-path scoped. The guard above refuses any pre-existing
        # instance of this exact executable; the main release test uses a unique copy.
        $shutdown = Start-Process -FilePath $exe -ArgumentList '--shutdown' -PassThru -WindowStyle Hidden
        $shutdown.WaitForExit(5000) | Out-Null
        $rootProcess.WaitForExit(5000) | Out-Null
        if (-not $rootProcess.HasExited) { Stop-Process -Id $rootProcess.Id -Force }
    }
    $env:LUMA_DATA_DIRECTORY = $oldDataDirectory
    $env:LUMA_TEST_SESSION = $oldTestSession
    if ((Hash-OrAbsent $userState) -ne $userStateBefore) { Write-Error 'User state changed during isolated soak.' }
    Write-Host "Reliability evidence: $resultFile"
}
