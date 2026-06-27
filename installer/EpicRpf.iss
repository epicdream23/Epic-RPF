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
; VERSIONING RULE: unless a specific version is requested, every new build bumps the
; MINOR by one tenth (4.1.0 -> 4.2.0 -> 4.3.0 -> ...). Only roll to a new MAJOR (e.g.
; 5.0.0) when explicitly told ("now that's v5"). Keep this in sync with <Version> in
; src\App.UI\App.UI.csproj.
#define MyAppVersion "4.2.7"
#define MyAppPublisher "Epic RPF"
#define MyAppExeName "EpicRpf.exe"
#define MyAppURL "https://discord.gg/FZw6ChbFbp"

[Setup]
AppId={{C7E9A3D4-5B21-4F6E-9C0D-2E8B71A4F3A9}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
AppCopyright=Copyright © 2026 Epic RPF
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
; File metadata written into the generated Setup.exe itself. An installer with proper
; company/product/description/version info looks far less suspicious to AV heuristics than
; a blank-metadata stub (one of the things engines like DeepInstinct weigh).
VersionInfoVersion={#MyAppVersion}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} Setup
VersionInfoCopyright=Copyright © 2026 Epic RPF
; force = if EpicRpf (or its WebView2 children) is still running during install/uninstall,
; forcibly terminate it so its locked .exe/.dll can actually be deleted. We ALSO taskkill the
; whole process tree in [Code] before deleting, as a belt-and-suspenders.
CloseApplications=force
RestartApplications=no

[Languages]
; The language picked here is also handed to the app (see CurStepChanged -> install.lang),
; so the tool starts in the same language. Keep names lowercase — LangCode() maps them.
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "portuguese"; MessagesFile: "compiler:Languages\Portuguese.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
; The two requested options — both visible checkboxes on the "Additional tasks" page.
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startmenuicon"; Description: "Create a &Start Menu shortcut"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "..\changelog.txt"; DestDir: "{app}"; Flags: ignoreversion
; CodeWalker companion world editor — its COMPLETE build output (CodeWalker.exe +
; SharpDX/Direct3D DLLs + Shaders\ + icons\ + config), so the "🗺 CodeWalker" button
; works on a fresh install. .NET Framework 4.8 (net48) ships with Windows 10/11.
Source: "..\CodeWalker\CodeWalker\bin\Release\net48\*"; DestDir: "{app}\CodeWalker"; Excludes: "*.pdb"; Flags: recursesubdirs ignoreversion
; WebView2 Evergreen bootstrapper — only executed when the runtime is missing (see [Run]).
Source: "redist\MicrosoftEdgeWebView2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startmenuicon
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\MicrosoftEdgeWebView2Setup.exe"; Parameters: "/silent /install"; \
  StatusMsg: "Installing Microsoft Edge WebView2 Runtime…"; Check: not WebView2Installed; \
  Flags: waituntilterminated
; Register the file-type associations + icons right after install (headless, no window) so the
; per-extension icons — incl. the new .rpf archive icon — are applied immediately, without the
; user having to launch the app first. EnsureRegistered() also pokes the shell to refresh icons.
; runasoriginaluser so the per-user (HKCU) keys land on the real user even if the install elevated.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--register"; \
  StatusMsg: "Registering file types…"; Flags: runhidden waituntilterminated runasoriginaluser
; Optional (checked by default) — opens changelog.txt in the user's default text
; editor on the final wizard page. Uncheck to skip; never runs on a silent install.
Filename: "{app}\changelog.txt"; Description: "View the changelog"; \
  Flags: shellexec nowait postinstall skipifsilent runasoriginaluser
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; \
  Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Full cleanup — remove EVERYTHING Epic RPF created: the WebView2 cache, the trash,
; custom names.json, saved settings, and the installer-language marker. (File associations
; are removed in code below.)
Type: filesandordirs; Name: "{localappdata}\EpicRpf"
Type: files; Name: "{app}\install.lang"

[Code]
// Tell the shell that file associations changed and rebuild the icon cache, so the custom
// per-extension icons (incl. .rpf) appear after install and DISAPPEAR after uninstall without
// needing a logoff/Explorer restart. SHCNE_ASSOCCHANGED = $08000000.
procedure SHChangeNotify(wEventId: Integer; uFlags: Cardinal; dwItem1, dwItem2: Cardinal);
  external 'SHChangeNotify@shell32.dll stdcall';
procedure RefreshShellIcons();
var rc: Integer;
begin
  try
    SHChangeNotify($08000000, $0000, 0, 0);
    Exec(ExpandConstant('{sys}\ie4uinit.exe'), '-show', '', SW_HIDE, ewWaitUntilTerminated, rc);
  except
  end;
end;

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

// Map the installer's chosen language (the [Languages] Name) to the app's language code.
function LangCode(): String;
var n: String;
begin
  n := ActiveLanguage;
  if n = 'german' then Result := 'de'
  else if n = 'french' then Result := 'fr'
  else if n = 'spanish' then Result := 'es'
  else if n = 'italian' then Result := 'it'
  else if n = 'dutch' then Result := 'nl'
  else if n = 'russian' then Result := 'ru'
  else if n = 'japanese' then Result := 'ja'
  else if (n = 'portuguese') or (n = 'brazilianportuguese') then Result := 'pt'
  else Result := 'en';
end;

// Hand the chosen language to the app: it reads {app}\install.lang on launch and starts in
// that language (until the user changes it in Settings). See MainWindow.BuildInject + appearance.js.
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SaveStringToFile(ExpandConstant('{app}\install.lang'), LangCode(), False);
end;

// Remove the file-association registry entries the app created for one extension (HKCU only;
// per-user associations). Only touches OUR ProgIds — never another app's default.
procedure CleanExt(ext: String);
var def: String;
begin
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\EpicRpf.' + ext);
  if RegQueryStringValue(HKCU, 'Software\Classes\.' + ext, '', def) and (def = 'EpicRpf.' + ext) then
    RegDeleteValue(HKCU, 'Software\Classes\.' + ext, '');
  RegDeleteValue(HKCU, 'Software\Classes\.' + ext + '\OpenWithProgids', 'EpicRpf.' + ext);
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.' + ext + '\UserChoice', 'ProgId', def) and (Copy(def, 1, 7) = 'EpicRpf') then
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.' + ext + '\UserChoice');
end;

// Force-terminate Epic RPF (and the WebView2 child processes it spawns) plus the bundled
// CodeWalker, so NOTHING is holding a handle to the .exe/.dll files we're about to delete.
// /T kills the whole process tree; /F forces it. taskkill on a not-running image is a
// harmless no-op. The Sleep lets Windows actually release the file handles before deletion.
procedure ForceCloseApp();
var rc: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM EpicRpf.exe',   '', SW_HIDE, ewWaitUntilTerminated, rc);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM CodeWalker.exe', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(1500);
end;

// On uninstall, strip every file association + ProgId + marker the app registered, so nothing
// of Epic RPF is left behind. (Keep this ext list in sync with FileAssoc.cs Exts.)
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var s, tok, app: String; p, i: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Kill the app BEFORE Inno tries to delete its files — otherwise the running .exe and
    // its loaded DLLs are locked and survive (the "could not delete all files" problem).
    ForceCloseApp();

    s := 'rpf ydr ydd yft ytd ypt ymt ymap ytyp ymf meta pso rbf gfx ynd ycd ybn awc rel dat cut ynv yed yfd ypdb mrf yld gxt2 ';
    while s <> '' do
    begin
      p := Pos(' ', s);
      tok := Copy(s, 1, p - 1);
      s := Copy(s, p + 1, Length(s));
      if tok <> '' then CleanExt(tok);
    end;
    // our own ProgIds, the version marker, and our own .epic format
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\EpicRpf.Epic');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\EpicRpf');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\.epic');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.epic\UserChoice');

    // Now that our ProgIds/defaults are gone, refresh the shell so the custom icons vanish.
    RefreshShellIcons();
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    // Wipe the ENTIRE install folder. Inno only removes the files it originally laid down,
    // but the app + bundled CodeWalker write more at runtime (CodeWalker config, compiled
    // shaders/asset caches, logs, …) so the folder is left full of stragglers. With the
    // process already force-killed above, nothing is locked, so DelTree clears everything.
    // We retry a few times in case Windows is slow to release handles. The only file that
    // can't go here is the still-running uninstaller (unins000.exe/.dat) — Inno deletes that
    // and the now-empty folder itself right after this step. Elevation matches the install
    // mode, so a per-machine (Program Files) uninstall already runs with admin rights.
    app := ExpandConstant('{app}');
    for i := 1 to 6 do
    begin
      DelTree(app, True, True, True);
      if not DirExists(app) then Break;
      Sleep(500);
    end;
  end;
end;
