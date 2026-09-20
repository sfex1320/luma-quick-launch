param(
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [Parameter(Mandatory = $true)][string]$SourceDirectory,
    [switch]$RunNativeTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
$runSubKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$approvedSubKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
$uninstallSubKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\LumaQuickLaunch_is1'
$valueName = 'LumaQuickLaunch'
$hkcu = [Microsoft.Win32.Registry]::CurrentUser
if ($hkcu.OpenSubKey($uninstallSubKey)) { throw 'A registered Luma installation already exists. Refusing to replace the user installation during smoke tests.' }
$id = [guid]::NewGuid().ToString('N')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "luma-installer-smoke-$id"
$installDir = Join-Path $testRoot 'installed'
$groupName = "Luma Installer Smoke $id"
$groupDir = Join-Path ([Environment]::GetFolderPath('Programs')) $groupName
$desktopLink = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Luma Quick Launch.lnk'
$dataDir = Join-Path $env:LOCALAPPDATA 'Luma'
$stateFile = Join-Path $dataDir 'state.json'
$seedFile = Join-Path $dataDir "installer-smoke-$id.txt"
$evidence = [ordered]@{ result = 'running'; installer = $installer; installerSha256 = (Get-FileHash -LiteralPath $installer).Hash; source = $source; testRoot = $testRoot; checks = @() }
$testProcess = $null
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $evidence.checks += $Message
    Write-Host "PASS $Message"
}
function SnapshotValue([string]$SubKey) {
    $key = $hkcu.OpenSubKey($SubKey)
    try {
        if ($key -and $key.GetValueNames() -contains $valueName) { return @{ exists = $true; value = $key.GetValue($valueName, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames); kind = $key.GetValueKind($valueName) } }
        return @{ exists = $false }
    } finally { if ($key) { $key.Dispose() } }
}
function RestoreValue([string]$SubKey, $Snapshot) {
    $key = $hkcu.CreateSubKey($SubKey)
    try { if ($Snapshot.exists) { $key.SetValue($valueName, $Snapshot.value, $Snapshot.kind) } else { $key.DeleteValue($valueName, $false) } }
    finally { $key.Dispose() }
}
function SetRun([string]$Value) {
    $key = $hkcu.CreateSubKey($runSubKey)
    try { $key.SetValue($valueName, $Value, [Microsoft.Win32.RegistryValueKind]::String) } finally { $key.Dispose() }
}
function FileHashOrAbsent([string]$Path) { if (Test-Path -LiteralPath $Path) { return (Get-FileHash -LiteralPath $Path).Hash }; return '<absent>' }
function InvokeInstall([string]$Label) {
    $process = Start-Process -FilePath $installer -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/SP-', '/NORESTART', '/NORESTARTAPPLICATIONS', '/MERGETASKS=!desktopicon', ('/DIR="' + $installDir + '"'), ('/GROUP="' + $groupName + '"'), ('/LOG="' + (Join-Path $testRoot "$Label-install.log") + '"')) -PassThru -Wait -WindowStyle Hidden
    Check ($process.ExitCode -eq 0) "$Label install exit 0"
}
function InvokeUninstall([string]$Label) {
    $process = Start-Process -FilePath (Join-Path $installDir 'unins000.exe') -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + (Join-Path $testRoot "$Label-uninstall.log") + '"')) -PassThru -Wait -WindowStyle Hidden
    return $process.ExitCode
}
$originalRun = SnapshotValue $runSubKey
$originalApproved = SnapshotValue $approvedSubKey
$originalDesktop = FileHashOrAbsent $desktopLink
$originalState = FileHashOrAbsent $stateFile
$oldTestExe = $env:LUMA_TEST_EXE
$oldDataDirectory = $env:LUMA_DATA_DIRECTORY
try {
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
    Set-Content -LiteralPath $seedFile -Value $id -Encoding UTF8
    RestoreValue $runSubKey @{ exists = $false }
    InvokeInstall 'fresh'
    $exe = Join-Path $installDir 'Luma.exe'
    Check (-not (SnapshotValue $runSubKey).exists) 'Fresh installation does not enable startup'
    $registration = $hkcu.OpenSubKey($uninstallSubKey)
    try {
        Check ($null -ne $registration) 'Current-user uninstall registration exists'
        Check ($registration.GetValue('InstallLocation').TrimEnd('\') -eq $installDir) 'Uninstall registration points to isolated installation'
    } finally { if ($registration) { $registration.Dispose() } }
    $files = @(Get-ChildItem -LiteralPath $source -File -Recurse | Where-Object Name -ne 'Start-Luma.cmd')
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($source.Length + 1)
        if ((FileHashOrAbsent $file.FullName) -ne (FileHashOrAbsent (Join-Path $installDir $relative))) { throw "Installed payload mismatch: $relative" }
    }
    Check $true "Installed payload matches all $($files.Count) source files"
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $groupDir 'Luma Quick Launch.lnk'))
    Check ($shortcut.TargetPath -eq $exe -and $shortcut.Arguments -eq '--settings' -and $shortcut.Description -eq 'Luma Quick Launch') 'Start menu shortcut has exact target, arguments and identity'
    Check ((FileHashOrAbsent $desktopLink) -eq $originalDesktop) 'Deselected desktop shortcut remains unchanged'
    if ($RunNativeTests) {
        $env:LUMA_TEST_EXE = $exe
        Push-Location $root
        try {
            & npm run test:native 2>&1 | Tee-Object -FilePath (Join-Path $testRoot 'installed-native.log')
            Check ($LASTEXITCODE -eq 0) 'Real installed executable passes native integration suite'
        } finally { Pop-Location; $env:LUMA_TEST_EXE = $oldTestExe }
    }
    # The real installer requests graceful exit through the exact-copy event.
    $env:LUMA_DATA_DIRECTORY = Join-Path $testRoot 'running-state'
    $testProcess = Start-Process -FilePath $exe -ArgumentList '--startup' -PassThru -WindowStyle Hidden
    $env:LUMA_DATA_DIRECTORY = $oldDataDirectory
    Start-Sleep -Seconds 3
    Check (-not $testProcess.HasExited) 'Isolated installed application is running'
    Check ((InvokeUninstall 'running') -eq 0) 'Uninstall gracefully closes the exact running installation'
    $testProcess.WaitForExit(5000) | Out-Null
    Check ($testProcess.HasExited -and -not (Test-Path -LiteralPath $exe)) 'Scoped shutdown closes installed process before removing files'
    $testProcess = $null
    InvokeInstall 'upgrade-base'
    $ownCommand = '"' + $exe + '" --startup'
    SetRun $ownCommand
    $approved = $hkcu.CreateSubKey($approvedSubKey)
    try { $approved.SetValue($valueName, [byte[]](3,0,0,0,0,0,0,0,0,0,0,0), [Microsoft.Win32.RegistryValueKind]::Binary) } finally { $approved.Dispose() }
    $foreignLink = Join-Path $groupDir 'Luma Quick Launch.lnk'
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($foreignLink)
    $shortcut.TargetPath = Join-Path $env:SystemRoot 'System32/notepad.exe'
    $shortcut.Arguments = ''
    $shortcut.Description = 'Unrelated shortcut created by isolated installer test'
    $shortcut.Save()
    $foreignLinkHash = FileHashOrAbsent $foreignLink
    InvokeInstall 'upgrade'
    Check ((FileHashOrAbsent $foreignLink) -eq $foreignLinkHash) 'Upgrade preserves unrelated same-name shortcut'
    Check ((SnapshotValue $runSubKey).value -eq $ownCommand) 'Upgrade preserves existing startup target'
    Check ((SnapshotValue $approvedSubKey).value[0] -eq 3) 'Upgrade preserves Task Manager disabled startup state'
    Check ((Get-Content -LiteralPath $seedFile -Raw).Trim() -eq $id -and (FileHashOrAbsent $stateFile) -eq $originalState) 'Upgrade preserves seeded user data and existing settings'
    $foreignCommand = '"' + (Join-Path $testRoot 'other-copy/Luma.exe') + '" --startup'
    SetRun $foreignCommand
    Check ((InvokeUninstall 'foreign-owner') -eq 0) 'Uninstall completes when this installation is closed'
    Check ((SnapshotValue $runSubKey).value -eq $foreignCommand) 'Uninstall preserves another copy startup registration'
    Check ((SnapshotValue $approvedSubKey).value[0] -eq 3) 'Uninstall preserves another copy startup approval state'
    Check (-not (Test-Path -LiteralPath $exe)) 'Uninstall removes installed executable'
    Check ((FileHashOrAbsent $foreignLink) -eq $foreignLinkHash) 'Uninstall preserves unrelated same-name shortcut'
    Remove-Item -LiteralPath $foreignLink
    InvokeInstall 'owned-cleanup'
    SetRun $ownCommand
    Check ((InvokeUninstall 'owned-cleanup') -eq 0) 'Second uninstall completes'
    Check (-not (Test-Path -LiteralPath (Join-Path $groupDir 'Luma Quick Launch.lnk'))) 'Uninstall removes owned start menu shortcut'
    Check (-not (SnapshotValue $runSubKey).exists -and -not (SnapshotValue $approvedSubKey).exists) 'Uninstall removes only its own startup registration and approval value'
    Check ($null -eq $hkcu.OpenSubKey($uninstallSubKey)) 'Uninstall removes current-user uninstall registration'
    Check ((Get-Content -LiteralPath $seedFile -Raw).Trim() -eq $id -and (FileHashOrAbsent $stateFile) -eq $originalState) 'Uninstall preserves seeded user data and existing settings'
    Check ((FileHashOrAbsent $desktopLink) -eq $originalDesktop) 'User desktop shortcut remains unchanged after smoke test'
    $evidence.result = 'passed'
} finally {
    $env:LUMA_TEST_EXE = $oldTestExe
    $env:LUMA_DATA_DIRECTORY = $oldDataDirectory
    if ($testProcess -and -not $testProcess.HasExited) { Stop-Process -Id $testProcess.Id -Force; $testProcess.WaitForExit(5000) | Out-Null }
    if (Test-Path -LiteralPath (Join-Path $installDir 'unins000.exe')) { try { InvokeUninstall 'cleanup' | Out-Null } catch { Write-Warning $_ } }
    RestoreValue $runSubKey $originalRun
    RestoreValue $approvedSubKey $originalApproved
    if (Test-Path -LiteralPath $seedFile) { Remove-Item -LiteralPath $seedFile }
    if ($foreignLink -and $foreignLinkHash -and (FileHashOrAbsent $foreignLink) -eq $foreignLinkHash) { Remove-Item -LiteralPath $foreignLink }
    if ((Test-Path -LiteralPath $groupDir) -and -not @(Get-ChildItem -LiteralPath $groupDir -Force).Count) { [IO.Directory]::Delete($groupDir, $false) }
    $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $testRoot 'result.json') -Encoding UTF8
    Write-Host "Installer evidence: $(Join-Path $testRoot 'result.json')"
}
