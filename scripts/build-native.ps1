# Luma 原生内核构建脚本
# 用法：powershell -ExecutionPolicy Bypass -File scripts/build-native.ps1 [-Configuration Release]
# 产物：APP/native/Luma/（自包含，无需 .NET 运行时与 Node.js），并打包 frontend dist。
param([string]$OutputDirectory, [string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$native = Join-Path $root 'native'
$outDir = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'APP\native\Luma' }
if ($env:LUMA_CONFIGURATION) { $configuration = $env:LUMA_CONFIGURATION }

# 定位 .NET 10 SDK：优先用户目录安装（dotnet-install），其次 PATH。
$dotnetCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'),
    (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source)
)
$dotnet = $null
foreach ($candidate in $dotnetCandidates) {
    if ($candidate -and (Test-Path $candidate)) {
        $dotnet = $candidate
        break
    }
}
if (-not $dotnet) { throw "未找到 dotnet；请先安装 .NET SDK 10（scripts/install-dotnet10.ps1）" }
$env:DOTNET_ROOT = Split-Path $dotnet -Parent
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
Write-Host "== dotnet: $(& $dotnet --version) =="

# global.json 固定 SDK 10.0.401+，在 native 目录内执行所有 dotnet 命令。

# 1) 前端生产构建（dist 打进交付包）
Write-Host "== npm run build =="
Push-Location $root
if (Test-Path (Join-Path $root 'node_modules')) {
    npm run build
    if ($LASTEXITCODE -ne 0) { Pop-Location; throw "前端构建失败" }
} elseif (Test-Path (Join-Path $root 'dist\index.html')) {
    Write-Host "node_modules 不存在，复用已有 dist/（终端用户无需 Node）"
} else {
    Pop-Location; throw "dist 不存在且无法构建；请先 npm ci && npm run build"
}
Pop-Location

# 2) 测试通过后才替换产物；SDK 选择必须在 global.json 所在目录内执行。
& (Join-Path $PSScriptRoot 'make-icon.ps1')
Push-Location $native
try {
Write-Host "== dotnet test =="
& $dotnet test (Join-Path $native 'Luma.sln') -c $configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "内核测试失败" }

Get-Process -Name Luma -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -eq (Join-Path $outDir 'Luma.exe')
} | ForEach-Object {
    Write-Host "== 停止本交付目录实例 pid=$($_.Id) =="
    Stop-Process -Id $_.Id -Force
    $_.WaitForExit(5000) | Out-Null
}

Write-Host "== dotnet publish (self-contained win-x64) =="
& $dotnet publish (Join-Path $native 'Luma.Host\Luma.Host.csproj') `
    -c $configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $outDir --nologo
if ($LASTEXITCODE -ne 0) { throw "发布失败" }
} finally { Pop-Location }

# 4) 打包 frontend dist
Write-Host "== 拷贝 dist =="
$frontendOut = Join-Path $outDir 'dist'
New-Item -ItemType Directory -Path $frontendOut -Force | Out-Null
Copy-Item -Path (Join-Path $root 'dist\*') -Destination $frontendOut -Recurse -Force
if ((Get-FileHash (Join-Path $root 'dist\index.html')).Hash -ne (Get-FileHash (Join-Path $frontendOut 'index.html')).Hash) {
    throw "打包前端与最新构建不一致"
}

# 5) 产物摘要
& (Join-Path $PSScriptRoot 'get-webview2-bootstrapper.ps1')
Copy-Item -LiteralPath (Join-Path $root 'releases/dependencies/MicrosoftEdgeWebview2Setup.exe') -Destination (Join-Path $outDir 'MicrosoftEdgeWebview2Setup.exe') -Force
$exe = Join-Path $outDir 'Luma.exe'
if (-not (Test-Path $exe)) { throw "发布产物缺少 Luma.exe" }
if (-not (Test-Path (Join-Path $outDir 'dist\index.html'))) { throw "发布产物缺少 dist\index.html" }
$sizeMb = [math]::Round(((Get-ChildItem $outDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host ""
Write-Host "== 构建完成 =="
Write-Host "交付目录：$outDir （$sizeMb MB）"
Write-Host "可执行：$exe"
& $dotnet --version | ForEach-Object { Write-Host "SDK：$_" }
