param([int]$TargetProcessId=0,[int]$Seconds=8)
$ErrorActionPreference='Stop'
if($TargetProcessId -gt 0 -and !(Get-Process -Id $TargetProcessId -ErrorAction SilentlyContinue)){throw 'Target process is not running; no zero-usage claim is valid.'}
function Snapshot {
  $ids = [System.Collections.Generic.HashSet[int]]::new()
  if ($TargetProcessId -gt 0) {
    [void]$ids.Add($TargetProcessId)
    $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId)
    do { $count=$ids.Count; foreach($p in $all) { if($ids.Contains([int]$p.ParentProcessId)){[void]$ids.Add([int]$p.ProcessId)} } } while($ids.Count -ne $count)
  }
  $cpu=0.0; $bytes=0L; $live=0
  foreach($processIdValue in $ids){$p=Get-Process -Id $processIdValue -ErrorAction SilentlyContinue;if($p){$cpu+=$p.TotalProcessorTime.TotalSeconds;$bytes+=$p.PrivateMemorySize64;$live++}}
  $dwm=0.0; foreach($p in @(Get-Process -Name dwm)){$dwm+=$p.TotalProcessorTime.TotalSeconds}
  return @{Cpu=$cpu;Bytes=$bytes;Count=$live;Dwm=$dwm;At=[DateTime]::UtcNow}
}
$a=Snapshot
Start-Sleep -Seconds $Seconds
$b=Snapshot
$wall=($b.At-$a.At).TotalSeconds
[ordered]@{targetPid=$TargetProcessId;seconds=[Math]::Round($wall,2);processes=$b.Count;privateMB=[Math]::Round($b.Bytes/1MB,1);cpuSingleCorePercent=[Math]::Round(($b.Cpu-$a.Cpu)/$wall*100,2);dwmSingleCorePercent=[Math]::Round(($b.Dwm-$a.Dwm)/$wall*100,2);logicalCores=[Environment]::ProcessorCount} | ConvertTo-Json -Compress
