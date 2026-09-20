param(
    [Parameter(Mandatory = $true)][string]$SourceDirectory,
    [string]$Version = '0.2.0',
    [string]$OutputDirectory,
    [string]$CompilerPath,
    [switch]$BootstrapCompiler
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Installer version must be major.minor.patch.' }
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
foreach ($required in @('Luma.exe', 'Luma.dll', 'Luma.runtimeconfig.json', 'dist/index.html', 'build-info.json', 'licenses')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $required))) { throw "Missing release payload: $required" }
}
$buildInfo = Get-Content -LiteralPath (Join-Path $source 'build-info.json') -Raw | ConvertFrom-Json
if ($buildInfo.version -ne $Version -or $buildInfo.platform -ne 'win-x64') { throw 'Installer requires a matching verified win-x64 release payload.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'releases' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$installer = Join-Path $output "luma-quick-launch-$Version-setup-x64.exe"
if (Test-Path -LiteralPath $installer) { throw "Refusing to replace existing release: $installer" }

# Pinned official Inno Setup 6.7.3; hash also published in the official .issig file.
$compilerVersion = '6.7.3'
$compilerHash = '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732'
$compilerUrl = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe'
$toolsDirectory = Join-Path $root 'releases/tools'
$compilerDirectory = Join-Path $toolsDirectory "InnoSetup-$compilerVersion"
if (-not $CompilerPath) { $CompilerPath = Join-Path $compilerDirectory 'ISCC.exe' }
if (-not (Test-Path -LiteralPath $CompilerPath)) {
    if (-not $BootstrapCompiler) { throw "Compiler not found: $CompilerPath. Use -BootstrapCompiler to provision the verified official portable compiler." }
    New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
    $download = Join-Path $toolsDirectory "innosetup-$compilerVersion.exe"
    if (-not (Test-Path -LiteralPath $download)) { Invoke-WebRequest -Uri $compilerUrl -OutFile $download }
    if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $compilerHash) { throw 'Inno Setup download SHA256 mismatch.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $download
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|, )CN=Pyrsys B\.V\.(,|$)') { throw 'Inno Setup publisher signature could not be verified.' }
    # Official portable mode creates no uninstaller, Start Menu entry, or file association.
    $process = Start-Process -FilePath $download -ArgumentList @('/PORTABLE=1', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER', '/NOICONS', ('/DIR="' + $compilerDirectory + '"')) -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw "Portable compiler provisioning failed: $($process.ExitCode)" }
}
$CompilerPath = (Resolve-Path -LiteralPath $CompilerPath).Path
$compilerSignature = Get-AuthenticodeSignature -LiteralPath $CompilerPath
if ($compilerSignature.Status -ne 'Valid' -or $compilerSignature.SignerCertificate.Subject -notmatch '(^|, )CN=Pyrsys B\.V\.(,|$)') { throw 'Compiler publisher signature could not be verified.' }
$compilerFiles = @{
    'ISCC.exe' = '0A8757031B33777E4C9CBFFEE40F11A5062B36D25CBE144C1DB73B6102B80AD7'
    'ISCmplr.dll' = '85A1E3090D3A5B85319F001B7C8F9ECFAD45F37EFF030A67BBE29EF58B7AA2C3'
}
foreach ($file in $compilerFiles.Keys) {
    if ((Get-FileHash -LiteralPath (Join-Path (Split-Path $CompilerPath) $file) -Algorithm SHA256).Hash -ne $compilerFiles[$file]) { throw 'Compiler files do not match pinned Inno Setup 6.7.3.' }
}
& $CompilerPath '/Qp' "/DSourceDirectory=$source" "/DAppVersion=$Version" "/DOutputDirectory=$output" (Join-Path $root 'installer/luma.iss')
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $installer)) { throw 'Installer compilation failed.' }
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$installer.sha256" -Encoding ASCII -Value "$hash  $([IO.Path]::GetFileName($installer))"
Write-Host "Built current-user installer: $installer"
Write-Host "SHA256: $hash"
