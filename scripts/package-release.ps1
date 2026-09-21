param([string]$Version = '0.3.0', [switch]$RequireSignature, [switch]$DeferDesktopChecks)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw 'Invalid release version.' }
$root = Split-Path $PSScriptRoot -Parent
$package = Get-Content (Join-Path $root 'package.json') -Raw | ConvertFrom-Json
[xml]$project = Get-Content (Join-Path $root 'native/Luma.Host/Luma.Host.csproj') -Raw
if ($package.version -ne $Version -or $project.Project.PropertyGroup.Version -ne $Version) {
    throw 'Release, frontend and native versions must match.'
}
$releaseDir = Join-Path $root 'APP'
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
$archiveName = "luma-quick-launch-$Version-win-x64"
$zip = Join-Path $releaseDir "$archiveName.zip"
if (Test-Path $zip) { throw "Already exists; refusing to replace: $zip" }
$buildDir = Join-Path (Join-Path $root 'releases') ('build-' + [guid]::NewGuid().ToString('N'))
$stage = Join-Path $buildDir $archiveName
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Push-Location $root
try {
    npm test
    if ($LASTEXITCODE -ne 0) { throw 'Frontend tests failed.' }
    & (Join-Path $PSScriptRoot 'build-native.ps1') -OutputDirectory $stage
    if ($RequireSignature) {
        & (Join-Path $PSScriptRoot 'sign-artifact.ps1') -Path @((Join-Path $stage 'Luma.exe'), (Join-Path $stage 'Luma.dll'))
    }
    Copy-Item -LiteralPath (Join-Path $root 'docs/portable-readme.txt') -Destination (Join-Path $stage 'README.txt')
    Set-Content -LiteralPath (Join-Path $stage 'Start-Luma.cmd') -Encoding ASCII -Value '@echo off', 'start "" "%~dp0Luma.exe" --settings'

    # Preserve the actual installed dependency licenses; do not fetch floating versions.
    $licenses = Join-Path $stage 'licenses'
    New-Item -ItemType Directory -Path $licenses | Out-Null
    function Copy-Notices([string]$Source, [string]$Name) {
        $notices = @(Get-ChildItem -LiteralPath $Source -File | Where-Object { $_.Name -match '^(LICENSE|NOTICE|THIRD-PARTY-NOTICES)(\.|$)' })
        if (-not $notices.Count) { throw "Missing dependency license: $Name" }
        $destination = Join-Path $licenses $Name
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination $destination }
    }
    foreach ($name in @('react', 'react-dom', 'scheduler', 'lucide-react', 'zod')) {
        $source = Join-Path $root "node_modules/$name"
        $dependency = Get-Content (Join-Path $source 'package.json') -Raw | ConvertFrom-Json
        Copy-Notices $source "$name-$($dependency.version)"
    }
    $assets = Get-Content (Join-Path $root 'native/Luma.Host/obj/project.assets.json') -Raw | ConvertFrom-Json
    $packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
    $nativePackages = @($project.Project.ItemGroup.PackageReference | Where-Object { $_ } | ForEach-Object { "$($_.Include.ToLowerInvariant())/$($_.Version)" })
    $runtime = Get-Content (Join-Path $stage 'Luma.runtimeconfig.json') -Raw | ConvertFrom-Json
    foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
        $nativePackages += "$($framework.name.ToLowerInvariant()).runtime.win-x64/$($framework.version)"
    }
    foreach ($name in $nativePackages) {
        $source = $packageRoots | ForEach-Object { Join-Path $_ $name } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (-not $source) { throw "Missing NuGet dependency: $name" }
        Copy-Notices $source ($name.Replace('/', '-'))
    }
    $commit = (& git rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0) { throw 'Commit the source before packaging.' }
    [ordered]@{ product = 'Luma Quick Launch'; version = $Version; platform = 'win-x64'; commit = $commit; signed = [bool]$RequireSignature; builtAtUtc = [DateTime]::UtcNow.ToString('o') } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'build-info.json') -Encoding UTF8

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    $verification = Join-Path $buildDir 'verification'
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $verification)
    $extracted = Join-Path $verification $archiveName
    $files = @(Get-ChildItem -LiteralPath $stage -Recurse -File)
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($stage.Length + 1)
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $extracted $relative)).Hash) {
            throw "Archive integrity failure: $relative"
        }
    }
    if (@(Get-ChildItem -LiteralPath $extracted -Recurse -File).Count -ne $files.Count) { throw 'Archive file count mismatch.' }

    # Run the extracted executable, with isolated state, instead of a development host.
    if (-not $DeferDesktopChecks) {
    & (Join-Path $PSScriptRoot 'assert-delivery-window.ps1')
    $installedExe = Join-Path $root 'APP/Luma/Luma.exe'
    $running = @(Get-Process -Name Luma -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installedExe })
    $previousTestExe = $env:LUMA_TEST_EXE
    $testLog = Join-Path $buildDir 'native-smoke.log'
    try {
        foreach ($process in $running) { Stop-Process -Id $process.Id -Force; $process.WaitForExit(5000) | Out-Null }
        $env:LUMA_TEST_EXE = Join-Path $extracted 'Luma.exe'
        # Merge native streams before PowerShell 5.1 sees them (Node stack traces
        # can otherwise be misinterpreted as PowerShell CLI XML).
        cmd.exe /d /c "npm run test:native 2>&1" | Tee-Object -FilePath $testLog
        if ($LASTEXITCODE -ne 0) { throw "Native integration tests failed; do not publish. See $testLog" }
        $logText = Get-Content -LiteralPath $testLog -Raw
        $jsonStart = $logText.IndexOf('{')
        if ($jsonStart -lt 0) { throw 'Missing native test evidence.' }
        $evidence = $logText.Substring($jsonStart) | ConvertFrom-Json
        if ($evidence.result -ne 'passed') { throw 'Native integration did not report success.' }
        $evidence | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $buildDir 'native-smoke.json') -Encoding UTF8
        cmd.exe /d /c "node native/tests/native-integration-smoke.mjs 2>&1" | Tee-Object -FilePath (Join-Path $buildDir 'integration-smoke.log')
        if ($LASTEXITCODE -ne 0) { throw 'Real desktop/startup integration failed; do not publish.' }
        cmd.exe /d /c "node native/tests/native-lifecycle-smoke.mjs 2>&1" | Tee-Object -FilePath (Join-Path $buildDir 'lifecycle-smoke.log')
        if ($LASTEXITCODE -ne 0) { throw 'Native startup/shutdown lifecycle failed; do not publish.' }
    } finally {
        $env:LUMA_TEST_EXE = $previousTestExe
        # Only close Explorer windows opened under this run's exact temporary data root.
        if (Test-Path $testLog) {
            try {
                $logText = Get-Content -LiteralPath $testLog -Raw
                $testData = ($logText.Substring($logText.IndexOf('{')) | ConvertFrom-Json).data
                $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
                $testRoot = [IO.Path]::GetFullPath($testData).TrimEnd('\') + '\'
                if ($testRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $testData -Leaf) -like 'luma-native-smoke-*') {
                    $shell = New-Object -ComObject Shell.Application
                    foreach ($window in @($shell.Windows())) {
                        try {
                            $uri = [uri]$window.LocationURL
                            if ($uri.IsFile -and $uri.LocalPath.StartsWith($testRoot, [StringComparison]::OrdinalIgnoreCase)) { $window.Quit() }
                        } catch { }
                    }
                }
            } catch { Write-Warning 'Could not inspect temporary Explorer windows for cleanup.' }
        }
        if ($running.Count) { Start-Process -FilePath $installedExe -ArgumentList '--settings' -WorkingDirectory (Split-Path $installedExe) -WindowStyle Hidden }
    }
    }
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$zip.sha256" -Encoding ASCII -Value "$hash  $archiveName.zip"
    Write-Host "Archive hash verified: $zip"
    Write-Host "Extracted verification directory: $extracted"
    if ($DeferDesktopChecks) { Write-Host "Desktop checks DEFERRED: $($files.Count) files matched; do not claim native acceptance or replace the live app yet." }
    else { Write-Host "Archive verified: $($files.Count) files; native checks: $($evidence.checks.Count)" }
    Write-Host "SHA256: $hash"
} finally { Pop-Location }
