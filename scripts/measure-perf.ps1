# Luma 内核性能测量：宿主 + 全部 WebView2 子进程的私有内存与 CPU，DWM 相对增量。
# 用法：powershell -ExecutionPolicy Bypass -File scripts/measure-perf.ps1
# 输出：docs/perf-report.md（真实采样数据，不使用预设数字）
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'APP\Luma\Luma.exe'
if (-not (Test-Path $exe)) { throw "未找到 $exe；请先运行 scripts/build-native.ps1" }
$stateDir = Join-Path $env:LOCALAPPDATA 'Luma'
$statePath = Join-Path $stateDir 'state.json'
$report = New-Object System.Text.StringBuilder
function Log([string]$line) { Write-Host $line; [void]$report.AppendLine($line) }

function Set-StateAutoHide([bool]$value) {
    if (Test-Path $statePath) {
        $json = Get-Content $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
        $json.preferences.autoHide = $value
        $json | ConvertTo-Json -Depth 10 | Out-File $statePath -Encoding utf8
    } else {
        @{ schemaVersion = 1; revision = 0; projects = @();
           preferences = @{ width = 640; height = 88; iconSize = 40; radius = 22; material = 'frost'; theme = 'light'; reducedMotion = $false; autoHide = $value }
        } | ConvertTo-Json -Depth 10 | Out-File $statePath -Encoding utf8
    }
}

function Get-ChildTree([int]$parentId) {
    $all = @()
    $queue = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$parentId" -ErrorAction SilentlyContinue)
    while ($queue.Count -gt 0) {
        $next = @()
        foreach ($p in $queue) {
            $all += $p
            $next += Get-CimInstance Win32_Process -Filter "ParentProcessId=$($p.ProcessId)" -ErrorAction SilentlyContinue
        }
        $queue = $next
    }
    return $all
}

function Sample-Luma([int]$seconds, [string]$label) {
    Start-Sleep -Milliseconds 1500  # 等场景稳定
    $luma = Get-Process -Name Luma -ErrorAction SilentlyContinue
    if (-not $luma) { Log "| $label | 进程未运行 | - | - | - |"; return }
    $children = Get-ChildTree $luma[0].Id
    $procs = @($luma) + @($children | ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
    $t0 = Get-Date
    $cpu0 = ($procs | Measure-Object -Property TotalProcessorTime -Sum).Sum.TotalSeconds
    $mem0 = ($procs | Measure-Object -Property PrivateMemorySize64 -Sum).Sum
    Start-Sleep -Seconds $seconds
    $procs = @($luma) + @(Get-ChildTree $luma[0].Id | ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
    $t1 = Get-Date
    $cpu1 = ($procs | Measure-Object -Property TotalProcessorTime -Sum).Sum.TotalSeconds
    $mem1 = ($procs | Measure-Object -Property PrivateMemorySize64 -Sum).Sum
    $wall = ($t1 - $t0).TotalSeconds
    $cpuPct = [math]::Round((($cpu1 - $cpu0) / $wall) * 100, 2)   # 相对单核；整机 16C 时占空比 = /16
    $mem = [math]::Round(($mem0 + $mem1) / 2 / 1MB, 1)
    $dwms = Get-Process -Name dwm -ErrorAction SilentlyContinue
    $dwmCpu0 = ($dwms | Measure-Object -Property TotalProcessorTime -Sum).Sum.TotalSeconds
    Start-Sleep -Seconds 2
    $dwms = Get-Process -Name dwm -ErrorAction SilentlyContinue
    $dwmCpu1 = ($dwms | Measure-Object -Property TotalProcessorTime -Sum).Sum.TotalSeconds
    $dwmPct = [math]::Round((($dwmCpu1 - $dwmCpu0) / 2) * 100, 2)
    Log ("| {0} | {1} 个进程 | {2} MB | {3}% 单核 | DWM {4}% 单核 |" -f $label, $procs.Count, $mem, $cpuPct, $dwmPct)
    return @{ Mem = $mem; Procs = $procs.Count }
}

Log "# Luma 内核性能实测"
Log ""
Log ("日期：{0}  设备：{1}  OS：{2}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm'), $env:COMPUTERNAME, [System.Environment]::OSVersion.Version)
Log "测量对象：Luma.exe 进程树（宿主 + 全部 WebView2/msedgewebview2 子进程）私有内存合计、CPU 时间差分。"
Log "CPU 百分比为单核口径；整机百分比为该值除以逻辑核心数（$([Environment]::ProcessorCount) 核）。"
Log ""
Log "| 场景 | 进程数 | 私有内存合计 | CPU(单核) | DWM |"
Log "|---|---|---|---|---|"

# ---- 0) DWM 基线（Luma 未运行）----
Get-Process -Name Luma -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$dwms = Get-Process -Name dwm -ErrorAction SilentlyContinue
$dwm0 = ($dwms | Measure-Object -Property TotalProcessorTime -Sum).Sum.TotalSeconds
Start-Sleep -Seconds 8
$dwms = Get-Process -Name dwm -ErrorAction SilentlyContinue
$dwm1 = ($dwms | Measure-Object -Property TotalProcessorTime -Sum).Sum.TotalSeconds
$dwmBase = [math]::Round((($dwm1 - $dwm0) / 8) * 100, 2)
Log ("| 基线（未运行 Luma） | - | - | - | DWM {0}% 单核 |" -f $dwmBase)
Log ""

# ---- 1) 隐藏态（autoHide=true，正常驻留）----
Set-StateAutoHide $true
Start-Process $exe
Log "### 场景：隐藏（收起静置 15s）"
Sample-Luma 15 "隐藏静置"

# ---- 2) 展开静止（autoHide=false + 唤出）----
Get-Process -Name Luma -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
Set-StateAutoHide $false
Start-Process $exe -ArgumentList '--show-dock'
Log "### 场景：展开静止（autoHide=false 常驻显示 15s）"
Sample-Luma 15 "展开静止"

# ---- 3) 200 次展开收起 ----
Get-Process -Name Luma -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
Set-StateAutoHide $true
Start-Process $exe
Start-Sleep -Seconds 4
Log "### 场景：200 次展开收起（激活信号驱动，完整 UI 往返）"
$before = Get-Process -Name Luma -ErrorAction SilentlyContinue
$beforeChildren = Get-ChildTree $before[0].Id
$beforeProcs = @($before) + @($beforeChildren | ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
$memBefore = [math]::Round((($beforeProcs | Measure-Object -Property PrivateMemorySize64 -Sum).Sum / 1MB), 1)
$evt = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::AutoReset, 'Luma.ProjectDock.Activate')
$sw = [System.Diagnostics.Stopwatch]::StartNew()
for ($i = 0; $i -lt 200; $i++) {
    $evt.Set() | Out-Null
    Start-Sleep -Milliseconds 700
}
$sw.Stop()
Start-Sleep -Seconds 3
$after = Get-Process -Name Luma -ErrorAction SilentlyContinue
$afterChildren = Get-ChildTree $after[0].Id
$afterProcs = @($after) + @($afterChildren | ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
$memAfter = [math]::Round((($afterProcs | Measure-Object -Property PrivateMemorySize64 -Sum).Sum / 1MB), 1)
Log ("200 次展开收起完成，耗时 {0:N1}s；内存 {1} MB → {2} MB（增量 {3} MB）" -f $sw.Elapsed.TotalSeconds, $memBefore, $memAfter, [math]::Round($memAfter - $memBefore, 1))

# ---- GPU 计数器可用性 ----
Log ""
Log "### GPU"
try {
    $gpuCounters = (Get-Counter '\GPU Engine(*)\Utilization Percentage' -ErrorAction Stop).CounterSamples | Where-Object { $_.CookedValue -gt 0 }
    if ($gpuCounters) {
        $total = [math]::Round(($gpuCounters | Measure-Object CookedValue -Sum).Sum, 2)
        Log "GPU Engine 总利用率（系统全量，含所有应用）：$total%"
        Log "注：Per-GPU 无按进程稳定 API，浮岛场景 GPU 占用以引擎总量对比基线评估。"
    } else { Log "GPU Engine 计数器存在但当前活动为 0（浮岛隐藏/合成空闲）。" }
} catch {
    Log "GPU Engine 计数器不可用：$($_.Exception.Message)（记为限制）"
}

Get-Process -Name Luma -ErrorAction SilentlyContinue | Stop-Process -Force

# 恢复 state.json 的 autoHide 为 true（用户默认）
Set-StateAutoHide $true

$outPath = Join-Path $root 'docs\perf-report.md'
[System.IO.File]::WriteAllText($outPath, $report.ToString(), [System.Text.Encoding]::UTF8)
Write-Host "报告已写入 $outPath"
