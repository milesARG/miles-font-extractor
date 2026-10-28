; Miles Font Extractor installer. Build with:  ISCC.exe installer\setup.iss  (after running build.ps1 to create .\publish)
#define AppName "Miles Font Extractor"
#define AppVersion "1.0.0"
#define AppExe "MilesFontExtractor.exe"

[Setup]
AppId={{6B0C1E5A-3F3D-4C57-9A58-0F7C2E4B7D11}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Miles
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=..\dist
OutputBaseFilename=MilesFontExtractor-Setup-{#AppVersion}
SetupIconFile=..\src\MilesFontExtractor\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; A one-time tool: install for the current user, no admin prompt and no install-mode dialog.
PrivilegesRequired=lowest
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

; No startup entry, no services, no background tasks: the app only runs when it is opened.
[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\MilesFontExtractor"
