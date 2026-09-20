param(
    [Parameter(Mandatory=$true)][string[]]$Path,
    [string]$CertificateThumbprint = $env:LUMA_SIGNING_THUMBPRINT,
    [string]$TimestampServer = 'http://timestamp.digicert.com',
    [string]$SignToolPath
)
$ErrorActionPreference = 'Stop'
if ($CertificateThumbprint -notmatch '^[0-9A-Fa-f]{40}$') { throw 'A real code-signing certificate thumbprint is required. No unsigned fallback.' }
$certificate = Get-Item -LiteralPath "Cert:/CurrentUser/My/$CertificateThumbprint" -ErrorAction Stop
if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le [DateTime]::Now -or $certificate.NotBefore -gt [DateTime]::Now) { throw 'Code-signing certificate is expired, not yet valid, or has no private key.' }
if (-not ($certificate.EnhancedKeyUsageList | Where-Object { $_.ObjectId.Value -eq '1.3.6.1.5.5.7.3.3' })) { throw 'Certificate does not allow code signing.' }
if (-not $SignToolPath) {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { $SignToolPath = $command.Source }
    else {
        $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
        if (Test-Path -LiteralPath $kits) {
            $SignToolPath = Get-ChildItem -LiteralPath $kits -Filter signtool.exe -Recurse -File |
                Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
        }
    }
}
if (-not $SignToolPath -or -not (Test-Path -LiteralPath $SignToolPath)) { throw 'Windows SDK SignTool is required.' }
$files = @($Path | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })
foreach ($file in $files) {
    if ([IO.Path]::GetExtension($file) -notin @('.exe','.dll')) { throw "Unsupported signing target: $file" }
    & $SignToolPath sign /sha1 $CertificateThumbprint /s My /fd SHA256 /tr $TimestampServer /td SHA256 $file
    if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed: $file" }
    & $SignToolPath verify /pa /all $file
    if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed: $file" }
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    if ($signature.Status -ne 'Valid' -or -not $signature.TimeStamperCertificate -or $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint) {
        throw "Trusted publisher/timestamp verification failed: $file"
    }
    Write-Host "Signed and verified: $file"
}
