; Inno Setup script for the Larik SQL Studio (WinUI) installer.
;
; Per-user install: no elevation, no admin rights, uninstalls from Apps & Features.
;
; Compile from the repository root:
;   ISCC.exe /DAppVersion=0.1.0 /DArch=x64 /DPublishDir=out\win-x64 installer\Larik.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef Arch
  #define Arch "x64"
#endif

#ifndef PublishDir
  #define PublishDir "..\out\win-" + Arch
#endif

#ifndef OutputDir
  #define OutputDir "..\out\installer"
#endif

[Setup]
AppId={{E701D625-402C-4E6D-AE74-0758E2C65A41}
AppName=Larik SQL Studio
AppVersion={#AppVersion}
AppVerName=Larik SQL Studio {#AppVersion}
AppPublisher=Larik
AppPublisherURL=https://github.com/dwifuady/larik-winui
AppSupportURL=https://github.com/dwifuady/larik-winui/issues
AppUpdatesURL=https://github.com/dwifuady/larik-winui/releases
VersionInfoVersion={#AppVersion}.0
VersionInfoProductVersion={#AppVersion}
VersionInfoProductName=Larik SQL Studio

; Per-user by default: installs to %LOCALAPPDATA%\Programs\Larik SQL Studio with no UAC prompt.
PrivilegesRequired=lowest
DefaultDirName={autopf}\Larik SQL Studio
DefaultGroupName=Larik SQL Studio
DisableProgramGroupPage=yes
UsePreviousAppDir=yes

; Windows 10 1809+ (same floor as the app).
MinVersion=10.0.17763

#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

OutputDir={#OutputDir}
OutputBaseFilename=Larik-{#AppVersion}-win-{#Arch}-setup
SetupIconFile=..\Larik\Assets\AppIcon.ico
UninstallDisplayIcon={app}\Larik.exe
; The Tauri app is also called "Larik SQL Studio" and installs side by side,
; so the WinUI build keeps a distinct Apps & Features name and shortcut.
UninstallDisplayName=Larik SQL Studio (WinUI) {#AppVersion}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no

LicenseFile=..\LICENSE
ChangesAssociations=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Larik SQL Studio (WinUI)"; Filename: "{app}\Larik.exe"
Name: "{autodesktop}\Larik SQL Studio (WinUI)"; Filename: "{app}\Larik.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Larik.exe"; Description: "{cm:LaunchProgram,Larik SQL Studio}"; Flags: nowait postinstall skipifsilent
