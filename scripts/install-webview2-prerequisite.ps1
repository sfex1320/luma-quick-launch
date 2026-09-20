param(
    [Parameter(Mandatory = $true)][string]$BootstrapperPath,
    [int]$TimeoutSeconds = 600
)
$ErrorActionPreference = 'Stop'
$bootstrapper = (Resolve-Path -LiteralPath $BootstrapperPath).Path
$process = Start-Process -FilePath $bootstrapper -ArgumentList @('/silent', '/install') -PassThru -WindowStyle Hidden
if ($process.WaitForExit($TimeoutSeconds * 1000)) { exit $process.ExitCode }

# Stop only the process created by this invocation and its descendants. This
# prevents a timed-out first attempt from overlapping a later retry.
& (Join-Path $env:SystemRoot 'System32\taskkill.exe') /PID $process.Id /T /F | Out-Null
try { $process.WaitForExit(5000) | Out-Null } catch { }
exit 1460
