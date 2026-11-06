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
PrivilegesRequiredOverridesAllowed=dialog
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

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if HasDesktopRuntime10 then
    Exit;

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
