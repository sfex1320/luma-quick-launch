$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
$scriptPath = Join-Path $env:TEMP 'dotnet-install.ps1'
Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $scriptPath
& $scriptPath -Channel 10.0 -InstallDir $installDir
Write-Host "=== Installed SDKs in $installDir ==="
& (Join-Path $installDir 'dotnet.exe') --list-sdks
