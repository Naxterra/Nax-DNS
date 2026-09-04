#define MyAppName "BetterDNS"
#define MyAppVersion "0.2.1"
#define MyAppPublisher "Naxterra"
#define MyAppURL "https://github.com/Naxterra/Better-DNS-Manager"
#define MyAppExeName "BetterDNS.exe"

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
CloseApplications=force
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
en.InstallServiceFailed=The BetterDNS service or signed kernel driver could not be installed. Setup will be rolled back.
de.InstallServiceFailed=Der BetterDNS-Dienst oder der signierte Kerneltreiber konnte nicht installiert werden. Die Installation wird zurückgesetzt.
en.UninstallServiceFailed=The BetterDNS service could not be removed. Uninstall was stopped to avoid leaving active DNS policy behind.
de.UninstallServiceFailed=Der BetterDNS-Dienst konnte nicht entfernt werden. Die Deinstallation wurde angehalten, damit keine aktive DNS-Richtlinie zurückbleibt.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\artifacts\BetterDNS\Service\*"; DestDir: "{app}\Service"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\BetterDNS\App\*"; DestDir: "{app}\App"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "install-service.ps1"; DestDir: "{app}\Installer"; Flags: ignoreversion
Source: "uninstall-service.ps1"; DestDir: "{app}\Installer"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\SECURITY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\BetterDNS"; Filename: "{app}\App\{#MyAppExeName}"; WorkingDir: "{app}\App"
Name: "{autodesktop}\BetterDNS"; Filename: "{app}\App\{#MyAppExeName}"; WorkingDir: "{app}\App"; Tasks: desktopicon

[Run]
Filename: "{app}\App\{#MyAppExeName}"; Description: "{cm:LaunchProgram}"; Flags: nowait postinstall skipifsilent shellexec

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

function RunLifecycleScript(const ScriptName: String): Boolean;
var
  ResultCode: Integer;
  Parameters: String;
begin
  Parameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' +
    AddQuotes(ExpandConstant('{app}\Installer\' + ScriptName)) + ' -InstallRoot ' +
    AddQuotes(ExpandConstant('{app}'));
  Result := Exec(PowerShellPath(), Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
    (ResultCode = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not StopExistingService() then
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
    if not RunLifecycleScript('uninstall-service.ps1') then
      RaiseException(CustomMessage('UninstallServiceFailed'));
end;
