; RWK Router/Keyer Installer
; Inno Setup Script
; Installs to Program Files\W1VE Software\RWK Router Keyer (admin elevation
; required for Windows Firewall rule creation).

#define MyAppName "RWK Router/Keyer"
; MyAppVersion may be overridden from the command line, e.g.
;   ISCC.exe /DMyAppVersion=1.0.6.24601 rwk-setup.iss
; so the installer's displayed/registered version matches the packaged binaries.
; publish.ps1 passes the verified staged FileVersion here.
; Fallback must be four-part (x.x.x.x) because VersionInfoVersion requires it.
; publish.ps1 always overrides this with the verified staged FileVersion.
#ifndef MyAppVersion
  #define MyAppVersion "1.0.5.0"
#endif
#define MyAppPublisher "Gerry Hull, W1VE"
#define MyAppURL "https://github.com/w1ve/rwk-router-keyer"

[Setup]
AppId={{A7F3B2E1-4D5C-4A8B-9E2F-1C3D5E7F9A0B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppCopyright=Copyright (c) 2026 Gerry Hull, W1VE. MIT License.
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany=W1VE
VersionInfoCopyright=Copyright (c) 2026 Gerry Hull, W1VE
VersionInfoProductName=RWK Router/Keyer
VersionInfoProductVersion={#MyAppVersion}
DefaultDirName={autopf}\W1VE Software\RWK Router Keyer
; Always use the new DefaultDirName, even if a previous version was installed to
; a different location (e.g. the old %LOCALAPPDATA% path). Without this, Inno
; reuses the remembered install directory and ignores DefaultDirName.
UsePreviousAppDir=no
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\..\artifacts\release
OutputBaseFilename=RWK-Setup
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
SetupIconFile=..\..\rwk.ico
UninstallDisplayIcon={app}\rwk.ico
; Read-only release-notes page shown up front (right after Welcome).
InfoBeforeFile=..\..\INSTALL_RELEASE_NOTES.txt
WizardStyle=modern
WizardImageFile=wizard-image.bmp
WizardSmallImageFile=wizard-small.bmp
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
Source: "..\..\INSTALL_RELEASE_NOTES.txt"; DestDir: "{app}"; Flags: ignoreversion
; Client
Source: "..\..\artifacts\release\staging\RWKClient.exe"; DestDir: "{app}"; Components: client; Flags: ignoreversion
Source: "..\..\artifacts\release\staging\Wizard\radios.json"; DestDir: "{app}\Wizard"; Components: client; Flags: ignoreversion
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

[Code]
function FindPreviousUninstaller(var UninstallString: String): Boolean;
var
  UninstallKey: String;
begin
  UninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{A7F3B2E1-4D5C-4A8B-9E2F-1C3D5E7F9A0B}_is1';
  // This installer runs elevated (PrivilegesRequired=admin), so an all-users install
  // records its uninstall key under HKLM, not HKCU. Check HKLM first (64-bit view via
  // HKLM64), then fall back to the 32-bit view and finally HKCU for installs made by an
  // older per-user build. Missing any of these must NOT abort setup.
  Result :=
    RegQueryStringValue(HKLM64, UninstallKey, 'UninstallString', UninstallString) or
    RegQueryStringValue(HKLM,   UninstallKey, 'UninstallString', UninstallString) or
    RegQueryStringValue(HKCU,   UninstallKey, 'UninstallString', UninstallString);
end;

function InitializeSetup(): Boolean;
var
  UninstallString: String;
  ResultCode: Integer;
begin
  Result := True;
  // Uninstall any previous version first so stale files/shortcuts can't linger and so a
  // relocated install directory is cleaned. The uninstall key lives in HKLM for this
  // admin installer; see FindPreviousUninstaller.
  if FindPreviousUninstaller(UninstallString) then
  begin
    // Run the uninstaller silently and wait for it to finish before laying down new files.
    Exec(RemoveQuotes(UninstallString), '/SILENT /NORESTART /SUPPRESSMSGBOXES', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  AppDir: String;
begin
  if CurStep = ssPostInstall then
  begin
    AppDir := ExpandConstant('{app}');
    // Add firewall rules for Client and Station executables.
    // Delete first to ensure clean state, then re-add with current path.
    if IsComponentSelected('client') then
    begin
      Exec('netsh', 'advfirewall firewall delete rule name="RWK Client"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Exec('netsh', 'advfirewall firewall add rule name="RWK Client" dir=in action=allow program="' + AppDir + '\RWKClient.exe" enable=yes profile=any', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
    if IsComponentSelected('station') then
    begin
      Exec('netsh', 'advfirewall firewall delete rule name="RWK Station"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Exec('netsh', 'advfirewall firewall add rule name="RWK Station" dir=in action=allow program="' + AppDir + '\RWKStation.exe" enable=yes profile=any', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
    // Sidecar also needs inbound access (tailnet connections arrive here)
    Exec('netsh', 'advfirewall firewall delete rule name="RWK Tailscale Sidecar"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('netsh', 'advfirewall firewall add rule name="RWK Tailscale Sidecar" dir=in action=allow program="' + AppDir + '\rwk-tailscale-sidecar.exe" enable=yes profile=any', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Clean up firewall rules on uninstall
    Exec('netsh', 'advfirewall firewall delete rule name="RWK Client"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('netsh', 'advfirewall firewall delete rule name="RWK Station"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('netsh', 'advfirewall firewall delete rule name="RWK Tailscale Sidecar"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;