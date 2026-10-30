; Orrery Setup, modelled on Prober's desktop setup (Prober.Installer\Prober.Desktop.iss): same wizard style
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

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=Copyright (c) 2026 InspireNXE
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
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

[Code]
#include "WizardLayout.iss"

procedure InitializeWizard;
begin
  SetUpWizardArt;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  ReflowActivePage;
end;
