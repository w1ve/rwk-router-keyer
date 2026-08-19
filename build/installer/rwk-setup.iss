; RWK Router/Keyer Installer
; Inno Setup Script
; Per-user install to {localappdata} — no admin rights required.

#define MyAppName "RWK Router/Keyer"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Gerry Hull, W1VE"
#define MyAppURL "https://github.com/w1ve/rwk-router-keyer"

[Setup]
AppId={{A7F3B2E1-4D5C-4A8B-9E2F-1C3D5E7F9A0B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={localappdata}\RWK Router Keyer
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\..\artifacts\release
OutputBaseFilename=RWK-Setup
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=lowest
SetupIconFile=..\..\rwk.ico
UninstallDisplayIcon={app}\rwk.ico
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "client"; Description: "Client only (operator position)"
Name: "station"; Description: "Station only (radio site)"
Name: "both"; Description: "Both Client and Station"

[Components]
Name: "client"; Description: "RWK Client (keyer/router at your operating position)"; Types: client both
Name: "station"; Description: "RWK Station (keyer output at the remote radio site)"; Types: station both
Name: "sidecar"; Description: "Tailscale Sidecar (required)"; Types: client station both; Flags: fixed

[Files]
; Sidecar — always installed
Source: "..\..\artifacts\release\staging\rwk-tailscale-sidecar.exe"; DestDir: "{app}"; Components: sidecar; Flags: ignoreversion
; Shared assets
Source: "..\..\artifacts\release\staging\splash.png"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\rwk.ico"; DestDir: "{app}"; Flags: ignoreversion
; Client
Source: "..\..\artifacts\release\staging\RWKClient.exe"; DestDir: "{app}"; Components: client; Flags: ignoreversion
; Station
Source: "..\..\artifacts\release\staging\RWKStation.exe"; DestDir: "{app}"; Components: station; Flags: ignoreversion

[Icons]
Name: "{group}\RWK Client"; Filename: "{app}\RWKClient.exe"; Components: client; IconFilename: "{app}\rwk.ico"
Name: "{group}\RWK Station"; Filename: "{app}\RWKStation.exe"; Components: station; IconFilename: "{app}\rwk.ico"
Name: "{group}\Uninstall RWK"; Filename: "{uninstallexe}"
Name: "{autodesktop}\RWK Client"; Filename: "{app}\RWKClient.exe"; Components: client; IconFilename: "{app}\rwk.ico"; Tasks: desktopicon
Name: "{autodesktop}\RWK Station"; Filename: "{app}\RWKStation.exe"; Components: station; IconFilename: "{app}\rwk.ico"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create desktop shortcuts"; GroupDescription: "Additional shortcuts:"

[Run]
Filename: "{app}\RWKClient.exe"; Description: "Launch RWK Client"; Components: client; Flags: nowait postinstall skipifsilent
Filename: "{app}\RWKStation.exe"; Description: "Launch RWK Station"; Components: station; Flags: nowait postinstall skipifsilent unchecked
