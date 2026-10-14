; Inno Setup script (https://jrsoftware.org/isinfo.php)
; Build:  1) run scripts\publish.ps1     2) compile this file with Inno Setup (ISCC.exe installer\Emberlight.iss)
; Output: artifacts\Emberlight-Setup-<version>.exe
; Installs the self-contained build per user (no admin rights needed) and optionally adds Emberlight to
; the "Open with" list for common image types. It does NOT take over your default image viewer.

#define MyAppName "Emberlight"
#define MyAppVersion "0.2.0"
#define MyAppExeName "Emberlight.exe"

[Setup]
AppId={{396FF38C-EA70-4490-9F15-24EBF144709D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Emberlight contributors
AppComments=A Windows port of Phoenix Slides by Dominic Yu
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts
OutputBaseFilename=Emberlight-Setup-{#MyAppVersion}
SetupIconFile=..\src\Emberlight.App\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=..\LICENSE-NOTICE.md
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked
Name: "openwith"; Description: "Add Emberlight to the ""Open with"" menu for images (JPEG, PNG, TIFF, BMP, GIF)"

[Files]
Source: "..\artifacts\selfcontained\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#MyAppName}"; Tasks: openwith; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".jpg"; ValueData: ""; Tasks: openwith; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".jpeg"; ValueData: ""; Tasks: openwith; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".jpe"; ValueData: ""; Tasks: openwith; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".jfif"; ValueData: ""; Tasks: openwith; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".png"; ValueData: ""; Tasks: openwith; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".tif"; ValueData: ""; Tasks: openwith; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".tiff"; ValueData: ""; Tasks: openwith; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".bmp"; ValueData: ""; Tasks: openwith; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".gif"; ValueData: ""; Tasks: openwith; Flags: uninsdeletekeyifempty

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
