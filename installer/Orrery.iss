; Orrery Setup, modeled on Prober's desktop setup (Prober.Installer\Prober.Desktop.iss): same wizard style
; and art, installed for everyone under Program Files. Build it with build\Build-Installer.ps1.
;
; The installed copy runs in installed mode (no orrery.portable.json beside it), so each person's
; connections, settings and credentials stay in their own profile (%APPDATA%\Orrery) and survive
; uninstalling and upgrading.

#ifndef AppVersion
  #error AppVersion must be defined. Pass /DAppVersion=<version> to ISCC (Build-Installer.ps1 does this for you).
#endif
#ifndef PublishDir
  #error PublishDir must be defined. Pass /DPublishDir=<path to the Orrery.App publish output> to ISCC (Build-Installer.ps1 does this for you).
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define MyAppName "Orrery"
#define MyAppPublisher "InspireNXE"
#define MyAppExe "Orrery.exe"
#define MyAppId "{{6E1B3F2A-9C47-4D85-B0E6-2F7A1C5D8E93}"
; Windows' version info takes numbers only, so a preview's "-preview.1" is left off there.
#if Pos("-", AppVersion) > 0
  #define NumericVersion Copy(AppVersion, 1, Pos("-", AppVersion) - 1)
#else
  #define NumericVersion AppVersion
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=Copyright (c) 2026 InspireNXE
VersionInfoVersion={#NumericVersion}
VersionInfoProductVersion={#NumericVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription={#MyAppName} setup
DefaultDirName={autopf}\Orrery
DefaultGroupName=Orrery
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableReadyPage=no
DisableWelcomePage=no
AllowNoIcons=yes
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern dynamic windows11
WizardImageFile=media\WizardImage.png
WizardImageFileDynamicDark=media\WizardImage.png
WizardSmallImageFile=
SetupIconFile=..\src\Orrery.App\Assets\orrery.ico
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=Orrery-Setup-{#AppVersion}
SetupLogging=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
SetupWindowTitle=Setup - %1 {#AppVersion}
ReadyLabel2a=Click Install to continue, or click Back to review or change your choices.
WelcomeLabel1=Welcome to [name] Setup
WelcomeLabel2=This will install [name] {#AppVersion}, the remote connection manager for RDP, SSH, VNC and Telnet, on this computer.%n%nEach person's connections and saved credentials stay in their own profile, so they're kept when Orrery is updated or removed.
WizardSelectTasks=Select additional tasks
WizardReady=Ready to install
WizardPreparing=Preparing to install
FinishedHeadingLabel=[name] Setup is finished

[Tasks]
Name: "desktopicon"; Description: "Orrery"; GroupDescription: "Desktop shortcuts:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "orrery.portable.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
; THIRD-PARTY-NOTICES.txt comes with the publish output (Build-Installer.ps1 generates it there).

[InstallDelete]
; A leftover marker would switch the installed copy into portable mode.
Type: files; Name: "{app}\orrery.portable.json"

[Icons]
Name: "{group}\Orrery"; Filename: "{app}\{#MyAppExe}"
Name: "{group}\Uninstall Orrery"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Orrery"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Start Orrery now"; Flags: nowait postinstall skipifsilent unchecked runasoriginaluser
; An update from inside Orrery runs setup with /VERYSILENT ... /RELAUNCH=1 (Orrery.Core's UpdateCommand), and this
; starts Orrery again afterwards, as the user who started setup rather than as administrator. That only works when
; setup was started without elevation and asked for it itself: an already elevated setup has no original user to go
; back to. --restarted makes the new Orrery wait for the old one to let go of the data folder.
Filename: "{app}\{#MyAppExe}"; Parameters: "--restarted"; Flags: nowait skipifnotsilent runasoriginaluser; Check: RelaunchAsked

[Code]
#include "WizardLayout.iss"

// /RELAUNCH=1 on setup's command line: start Orrery again after a silent install.
function RelaunchAsked: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

procedure InitializeWizard;
begin
  SetUpWizardArt;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  ReflowActivePage;
end;
