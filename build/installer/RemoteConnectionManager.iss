; Inno Setup script for Remote Connection Manager.
;
; Packs the folder that build/publish.ps1 writes (framework dependent, win-x64) into one setup exe.
; Build it with build/build-installer.ps1, which publishes first and passes the version in.
;
;   ISCC.exe /DAppVersion=1.2.3 /DSourceDir=..\..\artifacts\publish /DOutputDir=..\..\artifacts\installer RemoteConnectionManager.iss
;
; Installs per user by default (no admin prompt, under %LOCALAPPDATA%\Programs). The first page
; offers "install for all users" instead, which goes to Program Files and needs admin. Program
; Files is the easier path to ask for a Defender ASR exclusion on a PAW.
;
; Silent all users install for MDM (Intune and the like), always to
; C:\Program Files\RemoteConnectionManager:
;
;   RemoteConnectionManager-x.y.z-setup.exe /VERYSILENT /ALLUSERS /SUPPRESSMSGBOXES /NORESTART
;
; Running as SYSTEM without /ALLUSERS would install into the SYSTEM profile where nobody sees it,
; so setup refuses that case (exit code 1) instead. See README, "Deploying with an MDM".
;
; A default-settings.json next to the setup exe (or /DEFAULTSETTINGS=<file>) is copied to
; C:\ProgramData\RemoteConnectionManager, and RCM copies it to each new user's settings.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts\installer"
#endif

#define AppName "Remote Connection Manager"
#define AppExe "RemoteConnectionManager.exe"
#define AppPublisher "JamTheRadar"
#define AppUrl "https://github.com/jamtheradar/RemoteConnectionManager"

[Setup]
; Never change AppId: it is how upgrades and uninstall find an existing install.
AppId={{6F1C2B7E-4A3D-4E8B-9C21-5D7A0E3F8B14}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\RemoteConnectionManager
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
; commandline lets /ALLUSERS and /CURRENTUSER pick the mode with no dialog (silent installs).
PrivilegesRequiredOverridesAllowed=commandline dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
LicenseFile=..\..\LICENSE
SetupIconFile=..\..\src\RemoteConnectionManager.App\Resources\rcm.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir={#OutputDir}
OutputBaseFilename=RemoteConnectionManager-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Clean out the old program files on upgrade so removed DLLs don't linger. User data lives in
; %APPDATA% and %LOCALAPPDATA%\RemoteConnectionManager, never in {app}, so it is untouched.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Machine wide starting settings for new users (all users installs only). Taken from
; /DEFAULTSETTINGS=<file> or a default-settings.json next to the setup exe. Left behind on
; uninstall: it belongs to whoever deployed it, and users' own settings are copies anyway.
Source: "{code:DefaultSettingsSource}"; DestDir: "{commonappdata}\RemoteConnectionManager"; DestName: "default-settings.json"; Flags: external ignoreversion uninsneveruninstall; Check: ShouldCopyDefaultSettings

[InstallDelete]
Type: filesandordirs; Name: "{app}\*"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// The app is framework dependent, so it needs the .NET 10 Desktop Runtime (x64). Warn rather
// than block, since a silent install by an admin tool may put the runtime on afterwards.
function HasDesktopRuntime10: Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec) then
  begin
    try
      Result := (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0;
    finally
      FindClose(FindRec);
    end;
  end;
end;

// /DEFAULTSETTINGS=<file>, relative to the setup exe unless it has a drive or UNC share, or else
// default-settings.json next to the setup exe.
function DefaultSettingsSource(Param: String): String;
begin
  Result := ExpandConstant('{param:DEFAULTSETTINGS|}');
  if Result = '' then
    Result := ExpandConstant('{src}\default-settings.json')
  else if ExtractFileDrive(Result) = '' then
    Result := AddBackslash(ExpandConstant('{src}')) + Result;
end;

function ShouldCopyDefaultSettings: Boolean;
begin
  Result := IsAdminInstallMode and FileExists(DefaultSettingsSource(''));
end;

// SYSTEM's profile lives under Windows\System32\config\systemprofile whatever the language.
function RunningAsSystem: Boolean;
begin
  Result := Pos('\config\systemprofile', Lowercase(GetEnv('USERPROFILE'))) > 0;
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;

  // An MDM runs setup as SYSTEM. A per user install there lands in SYSTEM's own profile, so
  // no one gets the app. Fail loudly so the deployment shows as failed and gets fixed.
  if RunningAsSystem and not IsAdminInstallMode then
  begin
    Log('Running as SYSTEM without /ALLUSERS. Add /ALLUSERS to install to Program Files.');
    SuppressibleMsgBox(
      'Setup is running as SYSTEM, so it must install for all users.' + #13#10#13#10 +
      'Add /ALLUSERS to the command line.',
      mbCriticalError, MB_OK, IDOK);
    Result := False;
    Exit;
  end;

  // A named defaults file that isn't there is a packaging mistake: fail rather than install
  // without the settings the admin meant to deploy.
  if (ExpandConstant('{param:DEFAULTSETTINGS|}') <> '') and not FileExists(DefaultSettingsSource('')) then
  begin
    Log('Default settings file not found: ' + DefaultSettingsSource(''));
    SuppressibleMsgBox('Default settings file not found:' + #13#10 + DefaultSettingsSource(''),
      mbCriticalError, MB_OK, IDOK);
    Result := False;
    Exit;
  end;

  if FileExists(DefaultSettingsSource('')) and not IsAdminInstallMode then
    Log('Default settings are only installed for all users installs (/ALLUSERS). Ignoring ' + DefaultSettingsSource(''));

  if HasDesktopRuntime10 then
    Exit;

  // Silent installs never ask: the MDM is expected to deploy the runtime as a dependency.
  if WizardSilent then
  begin
    Log('The .NET 10 Desktop Runtime (x64) is not installed. Installing anyway; the app will not start until it is.');
    Exit;
  end;

  case SuppressibleMsgBox(
      'Remote Connection Manager needs the .NET 10 Desktop Runtime (x64), which isn''t installed.' + #13#10#13#10 +
      'Select Yes to open the download page, No to install anyway, or Cancel to stop.',
      mbConfirmation, MB_YESNOCANCEL, IDNO) of
    IDYES:
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    IDCANCEL:
      Result := False;
  end;
end;
