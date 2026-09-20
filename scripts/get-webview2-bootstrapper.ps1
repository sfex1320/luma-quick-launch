param(
    [string]$OutputPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'releases\dependencies\MicrosoftEdgeWebview2Setup.exe')
)
$ErrorActionPreference = 'Stop'
$officialUrl = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703'
$destination = [IO.Path]::GetFullPath($OutputPath)
$destinationDirectory = Split-Path $destination -Parent

function Test-MicrosoftSignature([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    return $signature.Status -eq 'Valid' -and
        $signature.SignerCertificate.Subject -match '(^|, )O=Microsoft Corporation(,|$)'
}

if (Test-MicrosoftSignature $destination) {
    Write-Host "Using verified Microsoft WebView2 bootstrapper: $destination"
    exit 0
}
if (Test-Path -LiteralPath $destination) {
    throw "Refusing to replace an invalid existing dependency: $destination"
}

New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
$download = Join-Path $destinationDirectory ('.webview2-' + [guid]::NewGuid().ToString('N') + '.download')
try {
    Invoke-WebRequest -Uri $officialUrl -OutFile $download -UseBasicParsing
    if (-not (Test-MicrosoftSignature $download)) {
        throw 'Downloaded WebView2 bootstrapper does not have a valid Microsoft Authenticode signature.'
    }
    Move-Item -LiteralPath $download -Destination $destination
} finally {
    if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force }
}

$hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Downloaded verified Microsoft WebView2 bootstrapper: $destination"
Write-Host "SHA256: $hash"
