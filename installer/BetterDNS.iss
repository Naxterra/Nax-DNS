#define MyAppName "BetterDNS"
#define MyAppVersion "0.5.5"
#define MyAppPublisher "Naxterra"
#define MyAppURL "https://github.com/Naxterra/Better-DNS-Manager"
#define MyAppExeName "BetterDNS.exe"
#ifndef BuildOutput
#define BuildOutput "..\artifacts\BetterDNS"
#endif

[Setup]
AppId={{8B0AD7A8-54CB-4E98-A707-8645171BE61D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\BetterDNS
DefaultGroupName=BetterDNS
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
SetupIconFile=..\src\BetterDns.Gui\Assets\BetterDNS.ico
OutputDir=..\artifacts\Installer
OutputBaseFilename=BetterDNS-Setup-{#MyAppVersion}-win-x64
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
WizardStyle=modern dark polar includetitlebar
WizardSizePercent=115
ShowLanguageDialog=auto
LanguageDetectionMethod=uilanguage
UsePreviousLanguage=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
UninstallDisplayIcon={app}\App\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=BetterDNS bilingual Windows installer
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
en.DesktopIcon=Create a &desktop shortcut
de.DesktopIcon=&Desktop-Verknüpfung erstellen
en.LaunchProgram=Launch BetterDNS
de.LaunchProgram=BetterDNS starten
en.StopServiceFailed=The existing BetterDNS service could not be stopped. Close the application and try again.
de.StopServiceFailed=Der vorhandene BetterDNS-Dienst konnte nicht beendet werden. Schließen Sie die Anwendung und versuchen Sie es erneut.
en.StopGuiFailed=The existing BetterDNS window could not close. Use Exit BetterDNS from its tray menu, then try again.
de.StopGuiFailed=Das vorhandene BetterDNS-Fenster konnte nicht beendet werden. Wählen Sie im Infobereich BetterDNS beenden und versuchen Sie es erneut.
en.InstallServiceFailed=BetterDNS did not pass its service and driver health check. Installed files may remain; protection is not confirmed. Details: %TEMP%\BetterDNS-installer.log
de.InstallServiceFailed=BetterDNS hat die Dienst- und Treiberprüfung nicht bestanden. Installierte Dateien können verbleiben; der Schutz ist nicht bestätigt. Details: %TEMP%\BetterDNS-installer.log
en.UninstallServiceFailed=The BetterDNS service could not be removed. Uninstall was stopped to avoid leaving active DNS policy behind.
de.UninstallServiceFailed=Der BetterDNS-Dienst konnte nicht entfernt werden. Die Deinstallation wurde angehalten, damit keine aktive DNS-Richtlinie zurückbleibt.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#BuildOutput}\Service\*"; DestDir: "{app}\Service"; Excludes: "*.pdb,WinDivert-2.2.2\*"; Flags: ignoreversion recursesubdirs createallsubdirs
; Immutable vendor files live in a versioned directory. Do not overwrite an identical loaded driver on repair.
Source: "{#BuildOutput}\Service\WinDivert-2.2.2\*"; DestDir: "{app}\Service\WinDivert-2.2.2"; Flags: onlyifdoesntexist recursesubdirs createallsubdirs uninsrestartdelete
Source: "{#BuildOutput}\App\*"; DestDir: "{app}\App"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "install-service.ps1"; DestDir: "{app}\Installer"; Flags: ignoreversion
Source: "uninstall-service.ps1"; DestDir: "{app}\Installer"; Flags: ignoreversion
Source: "manage-service.ps1"; DestDir: "{app}\Installer"; Flags: ignoreversion
Source: "supports-exit-for-update"; DestDir: "{app}\App"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\SECURITY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\BetterDNS"; Filename: "{app}\App\{#MyAppExeName}"; WorkingDir: "{app}\App"
Name: "{autodesktop}\BetterDNS"; Filename: "{app}\App\{#MyAppExeName}"; WorkingDir: "{app}\App"; Tasks: desktopicon

[Run]
Filename: "{app}\App\{#MyAppExeName}"; Description: "{cm:LaunchProgram}"; Flags: nowait postinstall skipifsilent shellexec runasoriginaluser

[UninstallDelete]
Type: filesandordirs; Name: "{app}\Service\.windivert-package"

[Code]
function PowerShellPath(): String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

function StopExistingService(): Boolean;
var
  ResultCode: Integer;
  Parameters: String;
begin
  Parameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ' +
    '"$ErrorActionPreference = ''Stop''; try { ' +
    '$s = Get-Service -Name ''BetterDNS'' -ErrorAction SilentlyContinue; ' +
    'if ($null -eq $s) { exit 0 }; ' +
    'if ($s.Status -ne ''Stopped'') { ' +
    'Stop-Service -Name ''BetterDNS'' -Force -ErrorAction Stop; ' +
    '$s.WaitForStatus(''Stopped'', [TimeSpan]::FromSeconds(20)) }; ' +
    'exit 0 } catch { Write-Error $_; exit 1 }"';
  Result := Exec(PowerShellPath(), Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
    (ResultCode = 0);
end;

function StopExistingGui(): Boolean;
var
  ResultCode: Integer;
  ExePath: String;
  Parameters: String;
begin
  Result := True;
  if not FileExists(ExpandConstant('{app}\App\supports-exit-for-update')) then
    Exit;
  ExePath := ExpandConstant('{app}\App\{#MyAppExeName}');
  if not FileExists(ExePath) then
    Exit;
  Result := Exec(ExePath, '--exit-for-update', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
    (ResultCode = 0);
  if not Result then
    Exit;
  Parameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ' +
    '"$ErrorActionPreference = ''Stop''; $p = @(Get-Process -Name ''BetterDNS'' -ErrorAction SilentlyContinue); ' +
    'if ($p.Count -gt 0) { $p | Wait-Process -Timeout 20 -ErrorAction Stop }"';
  Result := Exec(PowerShellPath(), Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
    (ResultCode = 0);
end;

function RunLifecycleScript(const ScriptName: String): Boolean;
var
  ResultCode: Integer;
  Parameters: String;
begin
  Parameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' +
    AddQuotes(ExpandConstant('{app}\Installer\' + ScriptName)) + ' -InstallRoot ' +
    AddQuotes(ExpandConstant('{app}')) + ' -LogPath ' +
    AddQuotes(ExpandConstant('{localappdata}\Temp\BetterDNS-installer.log'));
  Result := Exec(PowerShellPath(), Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
    (ResultCode = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not StopExistingGui() then
    Result := CustomMessage('StopGuiFailed')
  else if not StopExistingService() then
    Result := CustomMessage('StopServiceFailed');
end;

procedure SeedApplicationLanguage();
var
  SettingsDirectory: String;
  SettingsFile: String;
begin
  SettingsDirectory := ExpandConstant('{localappdata}\BetterDNS');
  SettingsFile := SettingsDirectory + '\ui-settings.json';
  if not FileExists(SettingsFile) then
  begin
    ForceDirectories(SettingsDirectory);
    SaveStringToFile(SettingsFile, '{"Language":"' + ActiveLanguage + '"}', False);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    SeedApplicationLanguage();
    if not RunLifecycleScript('install-service.ps1') then
      RaiseException(CustomMessage('InstallServiceFailed'));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    if not StopExistingGui() then
      RaiseException(CustomMessage('StopGuiFailed'))
    else if not RunLifecycleScript('uninstall-service.ps1') then
      RaiseException(CustomMessage('UninstallServiceFailed'));
end;
