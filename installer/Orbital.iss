; ============================================================================
;  Orbital — Inno Setup script  (per-user, no admin)
; ----------------------------------------------------------------------------
;  Orbital is a self-contained, single-file .NET 8 WPF exe. This installer:
;    * installs to %LOCALAPPDATA%\Programs\Orbital   (no admin rights needed)
;    * drops a Start Menu shortcut (and optional desktop shortcut)
;    * registers a proper uninstaller (Apps & Features / Settings > Apps)
;    * uses the branded orbital.ico for the installer + shortcuts
;
;  IT DELIBERATELY DOES NOT add a "Start with Windows" / Run-key entry.
;  Orbital's own in-app "Start with Windows" toggle owns that registry value
;  (HKCU\...\Run\Orbital -> "<exe>" --autostart). Adding it here too would
;  create a duplicate that fights the in-app toggle and points at a stale path.
;  The optional "launch at login" task below is provided for convenience and
;  simply pre-flips the SAME registry value the app uses, so they stay in sync.
;
;  Build:  iscc installer\Orbital.iss   (run from the repo root, AFTER publish)
;  See installer\README.md for the full publish + compile recipe.
; ============================================================================

#define MyAppName     "Orbital"
; Version can be overridden from the command line (publish.ps1 passes /DMyAppVersion=<csproj version>)
; so it stays in lockstep with OrbitalOverlay.csproj. Falls back to this default for a bare `iscc`.
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "Simon Kodu"
#define MyAppExeName  "OrbitalOverlay.exe"
; Path to the self-contained single-file publish output (relative to this .iss).
; Adjust if you publish elsewhere.
#define PublishDir    "..\bin\publish\win-x64"

[Setup]
; A stable, unique AppId. NEVER change this between releases or upgrades break.
AppId={{8F3A6C12-7B4E-4D9A-9E21-5C0A1F2B3D4E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}

; --- Per-user install, no UAC elevation ---
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableProgramGroupPage=yes
DefaultGroupName={#MyAppName}

; --- Output installer ---
OutputDir=..\bin\installer
OutputBaseFilename=Orbital-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes

; --- Branding / UX ---
SetupIconFile=..\orbital.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Close a running Orbital before overwriting its (locked) exe on upgrade.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
; Optional convenience: pre-enable launch-at-login by writing the SAME HKCU Run
; value the app's in-app toggle uses, so the two never disagree.
Name: "startuplogin"; Description: "Start {#MyAppName} automatically when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
; The single self-contained exe (publish produces just this one file + maybe a pdb).
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; Ship the icon alongside so shortcuts/uninstaller have a stable icon source.
Source: "..\orbital.ico"; DestDir: "{app}"; Flags: ignoreversion
; If WPF/runtime emits extra loose files in your publish dir, uncomment to grab them:
; Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\orbital.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\orbital.ico"; Tasks: desktopicon

[Registry]
; Only written if the user ticks the "Start automatically" task. Mirrors exactly
; what AutoStart.Set(true) writes inside the app, so the in-app toggle stays
; authoritative and consistent. Removed on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Orbital"; ValueData: """{app}\{#MyAppExeName}"" --autostart"; Flags: uninsdeletevalue; Tasks: startuplogin

[Run]
; Offer to launch right after install.
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Note: this does NOT touch %AppData%\Orbital\settings.json — user settings are
; intentionally preserved across reinstall/upgrade. Delete that folder manually
; for a truly clean wipe.
Type: dirifempty; Name: "{app}"
