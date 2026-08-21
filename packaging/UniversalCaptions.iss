; UniversalCaptions — Inno Setup installer script (per-user, offline, unsigned for this slice).
; Build: ISCC.exe /DStageDir=<staging root> /DAppVersion=0.5.44 UniversalCaptions.iss
; Install root: %LocalAppData%\UniversalCaptions (no admin, asInvoker at runtime). Short install
; path keeps every installed path well under the 260-char MAX_PATH limit.
; ADR-0011: Gemini-only pipeline — the closure is a single self-contained .NET publish.

#ifndef StageDir
  #error StageDir define is required (e.g. /DStageDir=C:\...\uc_pkg\Stage)
#endif

#define MyAppName "Universal Captions"
#ifndef AppVersion
  #define AppVersion "0.5.44"
#endif
#define MyAppPublisher "UniversalCaptions"
#define MyAppExeName "UniversalCaptions.App.exe"

[Setup]
AppId={{E3C9B2A1-8D4F-4E6B-9C2A-1F5D7B8A9C01}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\UniversalCaptions
DefaultGroupName=Universal Captions
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=output
OutputBaseFilename=UniversalCaptions-Setup-{#AppVersion}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=no
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#StageDir}\UniversalCaptions\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Universal Captions"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Universal Captions"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
