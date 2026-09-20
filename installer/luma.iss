#ifndef SourceDirectory
  #error SourceDirectory must name the verified win-x64 release directory.
#endif
#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif
#ifndef OutputDirectory
  #define OutputDirectory "..\releases"
#endif
#define AppId "LumaQuickLaunch"

[Setup]
AppId={#AppId}
AppName=Luma Quick Launch
AppVersion={#AppVersion}
AppPublisher=Luma Quick Launch
DefaultDirName={localappdata}\Programs\Luma Quick Launch
DefaultGroupName=Luma Quick Launch
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
DisableProgramGroupPage=no
AllowNoIcons=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
UninstallDisplayIcon={app}\Luma.exe
SetupIconFile=..\native\Luma.Host\Assets\luma.ico
OutputDir={#OutputDirectory}
OutputBaseFilename=luma-quick-launch-{#AppVersion}-setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=Luma.exe,*.dll
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "Start-Luma.cmd,MicrosoftEdgeWebview2Setup.exe"
Source: "{#SourceDirectory}\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDirectory}\MicrosoftEdgeWebview2Setup.exe"; Flags: dontcopy
Source: "..\scripts\install-webview2-prerequisite.ps1"; Flags: dontcopy

[Icons]
; We delete these ourselves only while they still point at this installed copy.
Name: "{autodesktop}\Luma Quick Launch"; Filename: "{app}\Luma.exe"; Parameters: "--settings"; WorkingDir: "{app}"; Comment: "Luma Quick Launch"; Tasks: desktopicon; Flags: uninsneveruninstall; Check: CanCreateShortcut('{autodesktop}\Luma Quick Launch.lnk')
Name: "{group}\Luma Quick Launch"; Filename: "{app}\Luma.exe"; Parameters: "--settings"; WorkingDir: "{app}"; Comment: "Luma Quick Launch"; Flags: uninsneveruninstall; Check: CanCreateShortcut('{group}\Luma Quick Launch.lnk')

[Run]
Filename: "{app}\Luma.exe"; Parameters: "--settings"; Description: "Open Luma Quick Launch"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunName = 'LumaQuickLaunch';
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppId}_is1';
var
  PreviousInstallDir: String;
  SkippedShortcut: Boolean;

function WebView2RuntimeInstalled(): Boolean;
var
  Version: String;
begin
  Result := False;
  if RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) then
    Result := (Version <> '') and (Version <> '0.0.0.0');
  if not Result then
    if RegQueryStringValue(HKLM32, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) then
      Result := (Version <> '') and (Version <> '0.0.0.0');
end;

function EnsureWebView2Runtime(var LastExitCode: Integer): Boolean;
var
  Attempt: Integer;
  Started: Boolean;
  Parameters: String;
begin
  Result := True;
  LastExitCode := 0;
  if WebView2RuntimeInstalled() then begin
    Log('Microsoft Edge WebView2 Runtime is already installed.');
    Exit;
  end;
  ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
  ExtractTemporaryFile('install-webview2-prerequisite.ps1');
  for Attempt := 1 to 2 do begin
    Log(Format('Installing the per-user WebView2 Runtime (attempt %d of 2).', [Attempt]));
    Parameters := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
      ExpandConstant('{tmp}\install-webview2-prerequisite.ps1') + '" -BootstrapperPath "' +
      ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe') + '" -TimeoutSeconds 600';
    Started := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Parameters,
      ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, LastExitCode);
    if Started and WebView2RuntimeInstalled() then Exit;
    if Started then Log(Format('WebView2 bootstrapper exit code: %d', [LastExitCode]))
    else Log('WebView2 bootstrapper could not be started.');
    if LastExitCode = 1460 then Break;
  end;
  Result := False;
end;

function CanCreateShortcut(const Path: String): Boolean;
var
  FileName: String;
  Shell, Shortcut: Variant;
begin
  FileName := ExpandConstant(Path);
  Result := not FileExists(FileName);
  if Result then Exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Shortcut := Shell.CreateShortcut(FileName);
    Result := (Shortcut.Arguments = '--settings') and
      ((CompareText(Shortcut.TargetPath, ExpandConstant('{app}\Luma.exe')) = 0) or
       ((CompareText(ExtractFileName(Shortcut.TargetPath), 'Luma.exe') = 0) and
        (Shortcut.Description = 'Luma Quick Launch')));
  except
    Result := False;
  end;
  if not Result then begin
    SkippedShortcut := True;
    Log('Preserving an unrelated existing shortcut: ' + FileName);
  end;
end;

function StartupCommand(const Exe: String): String;
begin
  Result := '"' + Exe + '" --startup';
end;

function OwnsRun(const Command, Exe: String): Boolean;
begin
  Result := CompareText(Trim(Command), StartupCommand(Exe)) = 0;
end;

function InitializeSetup(): Boolean;
begin
  RegQueryStringValue(HKCU, UninstallKey, 'InstallLocation', PreviousInstallDir);
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Command, Exe: String;
begin
  if CurStep = ssPostInstall then begin
    Exe := ExpandConstant('{app}\Luma.exe');
    if RegQueryStringValue(HKCU, RunKey, RunName, Command) then begin
      { Preserve an already registered installed copy during an upgrade/move.
        A portable copy's registration is deliberately left alone. The Windows
        StartupApproved state is preserved, including a Task Manager disable. }
      if OwnsRun(Command, Exe) or
        ((PreviousInstallDir <> '') and OwnsRun(Command, AddBackslash(PreviousInstallDir) + 'Luma.exe')) then
        if not RegWriteStringValue(HKCU, RunKey, RunName, StartupCommand(Exe)) then
          Log('Could not update the existing per-user startup registration.');
    end;
    if SkippedShortcut then
      SuppressibleMsgBox('An existing shortcut named Luma Quick Launch belongs to another application and was kept. Rename that shortcut if you want Luma to create its own entry. You can open Luma.exe from the installation folder.', mbInformation, MB_OK, IDOK);
  end;
end;

function CloseInstalledCopy(): Boolean;
var
  Locator, Services, Processes, Process: Variant;
  I, Attempt, ExitCode: Integer;
  Exe: String;
  Running: Boolean;
  VersionMS, VersionLS: Cardinal;
begin
  Result := False;
  Exe := ExpandConstant('{app}\Luma.exe');
  try
    { The switch was introduced in 0.2.0. Do not accidentally activate an older
      copy that interprets unknown switches as a normal launch. }
    if FileExists(Exe) and GetVersionNumbers(Exe, VersionMS, VersionLS) then
      if VersionMS >= 2 then
        if not Exec(Exe, '--shutdown', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) then
          Log('The scoped shutdown command could not be started.');
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('', 'root\CIMV2');
    for Attempt := 1 to 40 do begin
      Running := False;
      Processes := Services.ExecQuery('SELECT ExecutablePath FROM Win32_Process WHERE Name = "Luma.exe"');
      for I := 0 to Processes.Count - 1 do begin
        Process := Processes.ItemIndex(I);
        if not VarIsNull(Process.ExecutablePath) then
          if CompareText(Process.ExecutablePath, Exe) = 0 then Running := True;
      end;
      if not Running then begin
        Result := True;
        Exit;
      end;
      Sleep(250);
    end;
    Log('The exact installed copy is still running after the shutdown timeout.');
  except
    Log('Could not check whether this installed copy is running: ' + GetExceptionMessage);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  if not CloseInstalledCopy() then
    Result := 'Please close Luma Quick Launch from its tray menu, then try again. Your projects and settings will be kept.'
  else if not EnsureWebView2Runtime(ExitCode) then
    if ExitCode = 1460 then
      Result := 'Microsoft Edge WebView2 Runtime installation timed out and was stopped. Check the internet connection, then run Setup again.'
    else
      Result := Format('Microsoft Edge WebView2 Runtime could not be installed (exit code %d). Check the internet connection, then run Setup again.', [ExitCode]);
end;

function InitializeUninstall(): Boolean;
begin
  Result := CloseInstalledCopy();
  if not Result then
    SuppressibleMsgBox('Luma Quick Launch could not be closed automatically. Choose Exit from its tray menu, then run Uninstall again. Your projects and settings will be kept.', mbInformation, MB_OK, IDOK);
end;

procedure DeleteOwnedShortcut(const FileName, Exe: String);
var
  Shell, Shortcut: Variant;
begin
  if not FileExists(FileName) then Exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Shortcut := Shell.CreateShortcut(FileName);
    if (CompareText(Shortcut.TargetPath, Exe) = 0) and
      (Shortcut.Arguments = '--settings') and
      (Shortcut.Description = 'Luma Quick Launch') then
      DeleteFile(FileName);
  except
    Log('Leaving a shortcut whose ownership could not be verified: ' + FileName);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command, Exe: String;
begin
  if CurUninstallStep = usUninstall then begin
    Exe := ExpandConstant('{app}\Luma.exe');
    if RegQueryStringValue(HKCU, RunKey, RunName, Command) and OwnsRun(Command, Exe) then begin
      RegDeleteValue(HKCU, RunKey, RunName);
      RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', RunName);
    end;
    DeleteOwnedShortcut(ExpandConstant('{autodesktop}\Luma Quick Launch.lnk'), Exe);
    DeleteOwnedShortcut(ExpandConstant('{group}\Luma Quick Launch.lnk'), Exe);
  end;
  if CurUninstallStep = usPostUninstall then
    RemoveDir(ExpandConstant('{group}'));
end;
