param([Parameter(Mandatory=$true)][ValidateSet('Prepare','Inspect','DisableStartup','Restore')][string]$Action,
      [Parameter(Mandatory=$true)][string]$BackupDirectory)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$runPath = 'Software\Microsoft\Windows\CurrentVersion\Run'
$approvedPath = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
$entryName = 'LumaQuickLaunch'
$desktopLink = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Luma Quick Launch.lnk'
$record = Join-Path $BackupDirectory 'system-backup.json'
$originalLink = Join-Path $BackupDirectory 'original-desktop.lnk'
function Read-Entry([string]$KeyPath) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($KeyPath)
    try {
        if ($key -and $key.GetValueNames() -contains $entryName) {
            return @{ Exists=$true; Kind=[string]$key.GetValueKind($entryName); Value=$key.GetValue($entryName,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
        }
        return @{ Exists=$false }
    } finally { if ($key) { $key.Dispose() } }
}
function Restore-Entry([string]$KeyPath, $Entry) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($KeyPath)
    try {
        if ($Entry.Exists) {
            $value = $Entry.Value
            if ($Entry.Kind -eq 'Binary') { $value = [byte[]]$value }
            if ($Entry.Kind -eq 'DWord') { $value = [int]$value }
            if ($Entry.Kind -eq 'QWord') { $value = [long]$value }
            $key.SetValue($entryName,$value,[Microsoft.Win32.RegistryValueKind]$Entry.Kind)
        } else { $key.DeleteValue($entryName,$false) }
    } finally { $key.Dispose() }
}
if ($Action -eq 'Prepare') {
    if (Test-Path -LiteralPath $record) { throw 'Backup already exists.' }
    New-Item -ItemType Directory -Path $BackupDirectory -Force | Out-Null
    $exists = Test-Path -LiteralPath $desktopLink
    if ($exists) { Copy-Item -LiteralPath $desktopLink -Destination $originalLink }
    @{ Run=(Read-Entry $runPath); Approved=(Read-Entry $approvedPath); DesktopExists=$exists } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $record -Encoding UTF8
    Restore-Entry $runPath @{Exists=$false}; Restore-Entry $approvedPath @{Exists=$false}
    if ($exists) { Remove-Item -LiteralPath $desktopLink }
}
if ($Action -eq 'DisableStartup') {
    Restore-Entry $approvedPath @{Exists=$true;Kind='Binary';Value=[byte[]](3,0,0,0,0,0,0,0,0,0,0,0)}
}
if ($Action -eq 'Restore') {
    $backup = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
    Restore-Entry $runPath $backup.Run; Restore-Entry $approvedPath $backup.Approved
    if ($backup.DesktopExists) { Copy-Item -LiteralPath $originalLink -Destination $desktopLink -Force }
    elseif (Test-Path -LiteralPath $desktopLink) { Remove-Item -LiteralPath $desktopLink }
}
$linkInfo = $null
if (Test-Path -LiteralPath $desktopLink) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($desktopLink)
    $linkInfo = @{Path=$desktopLink;Target=$link.TargetPath;Arguments=$link.Arguments;WorkingDirectory=$link.WorkingDirectory;Description=$link.Description}
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
}
@{Run=(Read-Entry $runPath);Approved=(Read-Entry $approvedPath);Desktop=$linkInfo} | ConvertTo-Json -Depth 5 -Compress
