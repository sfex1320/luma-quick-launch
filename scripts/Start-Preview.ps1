param([switch]$NoBrowser)
$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectDir
$nodeCommand = Get-Command node -ErrorAction SilentlyContinue
if (-not $nodeCommand) { throw '需要 Node.js 22.12 或更高版本才能启动此前端预览。原生交付包不应依赖 Node.js。' }
if (-not (Test-Path -LiteralPath (Join-Path $projectDir 'dist/index.html'))) {
    if (-not (Test-Path -LiteralPath (Join-Path $projectDir 'node_modules'))) {
        & npm.cmd ci
        if ($LASTEXITCODE -ne 0) { throw '前端依赖安装失败。' }
    }
    & npm.cmd run build
    if ($LASTEXITCODE -ne 0) { throw '前端构建失败。' }
}
function Test-LumaPreview {
    try { return (Invoke-WebRequest -Uri 'http://127.0.0.1:4173/__luma_health' -UseBasicParsing -TimeoutSec 1).Content.Trim() -eq 'luma-preview-v1' } catch { return $false }
}
if (-not (Test-LumaPreview)) {
    $serverFile = Join-Path $PSScriptRoot 'preview-server.mjs'
    $serverProcess = Start-Process -FilePath $nodeCommand.Source -ArgumentList @('"' + $serverFile + '"') -WorkingDirectory $projectDir -WindowStyle Hidden -PassThru
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        if (Test-LumaPreview) { $ready = $true; break }
        if ($serverProcess.HasExited) { break }
        Start-Sleep -Milliseconds 150
    }
    if (-not $ready) { throw '本地预览启动失败，请检查 4173 端口是否被其他程序占用。' }
}
if (-not $NoBrowser) { Start-Process 'http://127.0.0.1:4173' }
