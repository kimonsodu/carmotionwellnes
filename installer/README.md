# Building the Orbital installer

Orbital ships as a **self-contained, single-file** Windows app plus a small
per-user Inno Setup installer. No admin rights, no .NET install required on the
target machine.

## TL;DR

```powershell
# 1. From the repo root: publish the self-contained single-file exe
"C:\Program Files\dotnet\dotnet.exe" publish .\OrbitalOverlay.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none `
  -o .\bin\publish\win-x64

# 2. Compile the installer (requires Inno Setup 6 -> iscc on PATH)
iscc .\installer\Orbital.iss
```

Outputs:

| Artifact | Path |
| --- | --- |
| Self-contained exe | `bin\publish\win-x64\OrbitalOverlay.exe` |
| Installer | `bin\installer\Orbital-Setup-1.0.0.exe` |

> Kill any running `OrbitalOverlay.exe` before publishing/compiling — the
> single-file exe self-extracts to a temp dir and locks files while running.

## About the publish flags

- `--self-contained true -r win-x64` bundles the .NET 8 runtime so end users
  don't need to install anything.
- `PublishSingleFile=true` + `IncludeNativeLibrariesForSelfExtract=true` rolls
  everything (including the native `coreclr`/WPF bits) into one `.exe`.
- `EnableCompressionInSingleFile=true` shrinks the ~150 MB self-contained
  bundle noticeably.
- **No trimming.** WPF is not trim-safe — `PublishTrimmed=true` strips XAML/
  reflection-loaded types and Orbital will crash at runtime (it loads
  `Theme.xaml` reflectively and uses WinForms interop). Leave trimming OFF.
- `DebugType=none` drops the `.pdb` so the publish folder is just the one exe.

## Why Inno Setup (vs MSIX / ClickOnce)

**Inno Setup — chosen.** Per-user (`PrivilegesRequired=lowest`), no admin
prompt, dead-simple to script, produces one tidy `Setup.exe`, gives a real
entry in Settings > Apps, and plays nicely with a self-contained single-file
exe. Zero ceremony.

- **MSIX** wants a signing cert to install at all (even sideloading needs a
  trusted cert + Developer Mode), runs the app in a container that can
  complicate the HKCU Run-key autostart and the always-on-top click-through
  overlay, and is heavier than this app warrants.
- **ClickOnce** is awkward for self-contained single-file WPF, has a clunky
  update/trust UX, and its manifest/signing story is more friction than value
  here.

For a single-exe, per-user, no-admin tool, Inno is the lowest-friction path.

## Code signing — the realistic picture

The installer and the app are **unsigned** out of the box. Consequences:

- **SmartScreen** will show *"Windows protected your PC"* on first run of the
  downloaded `Setup.exe`. Users must click *More info → Run anyway*. This is
  expected for any new unsigned publisher and fades as the file gains
  reputation (or instantly with an EV cert).
- Some browsers/AV may flag the download until it builds reputation.

### Self-sign (for *testing only* — does NOT remove SmartScreen for users)

```powershell
# Create a self-signed code-signing cert in your user store
$cert = New-SelfSignedCertificate -Type CodeSigningCert `
  -Subject "CN=Orbital Test" -CertStoreLocation Cert:\CurrentUser\My

# Sign the published exe (and/or the built Setup.exe)
$signtool = "C:\Program Files (x86)\Windows Kits\10\bin\x64\signtool.exe"
& $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /t http://timestamp.digicert.com `
  ".\bin\publish\win-x64\OrbitalOverlay.exe"
```

A self-signed cert is only trusted on machines where you've manually imported
it into *Trusted Root* — it does **not** satisfy SmartScreen for real users.
It's useful only to validate the signing pipeline.

### A real cert (to actually ship)

- Buy from a public CA (DigiCert, Sectigo, SSL.com, etc.).
- **Standard OV cert (~$100–300/yr):** signs valid, but still earns SmartScreen
  reputation slowly.
- **EV cert (~$300–600/yr, hardware token / cloud HSM):** grants immediate
  SmartScreen reputation — the cleanest user experience, no "unknown publisher"
  wall. Recommended if you distribute widely.
- Sign **both** the published `OrbitalOverlay.exe` and the final
  `Orbital-Setup-*.exe`, always with `/t` (a timestamp URL) so signatures stay
  valid after the cert expires.

## Autostart note

The installer **does not** add its own "start with Windows" entry by default.
Orbital's in-app **"Start with Windows"** toggle owns the
`HKCU\...\Run\Orbital` value (`"<exe>" --autostart`). The optional installer
*"Start automatically when I sign in"* task simply pre-writes that **same**
value so the two never disagree; it's removed on uninstall.
