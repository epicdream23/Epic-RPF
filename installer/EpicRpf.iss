; Epic RPF — Inno Setup installer.
; Build with tools\build-installer.ps1 (publishes the app self-contained first).
;
; What it bundles / guarantees:
;   - The complete self-contained publish (dist\publish): app + .NET runtime + wwwroot,
;     so the target machine needs NO .NET install.
;   - Microsoft Edge WebView2 Runtime: detected via registry; if missing, the bundled
;     Evergreen bootstrapper is run silently during install.
;   - Optional shortcuts (user-selectable checkboxes): Desktop and Start Menu.
;   - Installs per-user by default (no admin prompt, goes to %LOCALAPPDATA%\Programs);
;     "Install for all users" remains available via the privileges dialog.

#define MyAppName "Epic RPF"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Epic RPF"
#define MyAppExeName "EpicRpf.exe"

[Setup]
AppId={{C7E9A3D4-5B21-4F6E-9C0D-2E8B71A4F3A9}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=EpicRpf-Setup-{#MyAppVersion}
SetupIconFile=..\src\App.UI\icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
; The two requested options — both visible checkboxes on the "Additional tasks" page.
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startmenuicon"; Description: "Create a &Start Menu shortcut"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
; WebView2 Evergreen bootstrapper — only executed when the runtime is missing (see [Run]).
Source: "redist\MicrosoftEdgeWebView2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startmenuicon
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\MicrosoftEdgeWebView2Setup.exe"; Parameters: "/silent /install"; \
  StatusMsg: "Installing Microsoft Edge WebView2 Runtime…"; Check: not WebView2Installed; \
  Flags: waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; \
  Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Remove the WebView2 browser cache; keep real user data (names.json, trash) untouched.
Type: filesandordirs; Name: "{localappdata}\EpicRpf\WebView2"

[Code]
// The WebView2 Evergreen runtime registers under EdgeUpdate\Clients with a "pv"
// (product version) value — per-machine (both registry views) or per-user.
function WebView2Installed(): Boolean;
var
  pv: String;
begin
  Result :=
    (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', pv) and (pv <> '') and (pv <> '0.0.0.0')) or
    (RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', pv) and (pv <> '') and (pv <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', pv) and (pv <> '') and (pv <> '0.0.0.0'));
end;
