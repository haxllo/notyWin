; Inno Setup script for NotyWin (WPF)
; Install: a self-contained, single-folder install that puts the deck on
; every display without needing admin rights.

#define MyAppName "NotyWin"
#define MyAppDisplayName "Noty"
#define MyAppPublisher "Habeeb"
#define MyAppExeName "NotyWin.App.WPF.exe"
#define MyAppVersion "1.0.0"

[Setup]
AppId={{B0F36FA0-3FE2-4DB4-8D9D-8E47C7E5F2C0}
AppName={#MyAppDisplayName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputBaseFilename=NotyWin-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The publish output of NotyWin.App.WPF (win-x64 self-contained).
Source: "src\NotyWin.App.WPF\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppDisplayName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppDisplayName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppDisplayName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Per-user state — settings.json and notes.db live under LocalAppData.
; We do not delete them on uninstall so a user can reinstall without
; losing notes.
