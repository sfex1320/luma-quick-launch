param(
 [Parameter(Mandatory=$true)][ValidateSet('Prepare','Snapshot','Minimize','Maximize','Focus','Cleanup')][string]$Action,
 [Parameter(Mandatory=$true)][string]$FixtureRoot,
 [long]$WindowHandle = 0
)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false)
$resolvedRoot=[System.IO.Path]::GetFullPath($FixtureRoot)
if(-not ([System.IO.Path]::GetFileName($resolvedRoot).StartsWith('luma-reuse-smoke-'))) { throw 'Expected isolated reuse fixture directory' }
$testExe=[System.IO.Path]::GetFullPath((Join-Path $resolvedRoot 'probe/ReuseWindowProbe.exe'))
$testFolder=[System.IO.Path]::GetFullPath((Join-Path $resolvedRoot '同名目录')).TrimEnd('\')
$otherFolder=[System.IO.Path]::GetFullPath((Join-Path $resolvedRoot '其他/同名目录')).TrimEnd('\')
Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;
public static class LumaReuseProbe {
 [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr w);
 [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr w);
 [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr w,int mode);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr w);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr w,int index);
}
'@
if($Action -eq 'Prepare') {
 $wsh=New-Object -ComObject WScript.Shell
 try {
  $shortcut=$wsh.CreateShortcut((Join-Path $resolvedRoot 'fixture.lnk'))
  $shortcut.TargetPath=$testExe; $shortcut.WorkingDirectory=(Split-Path $testExe); $shortcut.Save()
  [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null
  $documentShortcut=$wsh.CreateShortcut((Join-Path $resolvedRoot 'document.lnk'))
  $documentShortcut.TargetPath=$testExe; $documentShortcut.Arguments='--document isolated-project'; $documentShortcut.Save()
  [Runtime.InteropServices.Marshal]::FinalReleaseComObject($documentShortcut) | Out-Null
 } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($wsh) | Out-Null }
 Write-Output '{}'; exit
}
if($Action -in @('Minimize','Maximize','Focus')) {
 $mode=if($Action -eq 'Minimize'){6}elseif($Action -eq 'Maximize'){3}else{9}
 [LumaReuseProbe]::ShowWindowAsync([IntPtr]$WindowHandle,$mode) | Out-Null
 if($Action -eq 'Focus'){ [LumaReuseProbe]::SetForegroundWindow([IntPtr]$WindowHandle) | Out-Null }
 Write-Output '{}'; exit
}
$shell=New-Object -ComObject Shell.Application
$collection=$null
$folders=@()
try {
 $collection=$shell.Windows()
 for($i=0;$i -lt $collection.Count;$i++) {
  $window=$null
  try {
   $window=$collection.Item($i)
   if(-not $window.LocationURL){continue}
   $uri=[uri]$window.LocationURL
   if(-not $uri.IsFile){continue}
   $location=[IO.Path]::GetFullPath($uri.LocalPath).TrimEnd('\')
   if($location -notin @($testFolder,$otherFolder)){continue}
   $handle=[long]$window.HWND
   if($Action -eq 'Cleanup') { $window.Quit(); continue }
   $folders+=@{path=$location;hwnd=$handle;minimized=[LumaReuseProbe]::IsIconic([IntPtr]$handle);maximized=[LumaReuseProbe]::IsZoomed([IntPtr]$handle)}
  } finally { if($null -ne $window){[Runtime.InteropServices.Marshal]::FinalReleaseComObject($window) | Out-Null} }
 }
} finally {
 if($null -ne $collection){[Runtime.InteropServices.Marshal]::FinalReleaseComObject($collection) | Out-Null}
 [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
}
$apps=@()
foreach($proc in @(Get-Process -Name ReuseWindowProbe -ErrorAction SilentlyContinue)) {
 if($proc.Path -ne $testExe){continue}
 if($Action -eq 'Cleanup') { Stop-Process -Id $proc.Id; continue }
 $handle=$proc.MainWindowHandle
 $apps+=@{pid=$proc.Id;hwnd=$handle.ToInt64();minimized=[LumaReuseProbe]::IsIconic($handle);maximized=[LumaReuseProbe]::IsZoomed($handle);topmost=(([LumaReuseProbe]::GetWindowLongPtr($handle,-20).ToInt64() -band 8) -ne 0)}
}
@{folders=$folders;apps=$apps;foreground=[LumaReuseProbe]::GetForegroundWindow().ToInt64()} | ConvertTo-Json -Depth 5 -Compress
