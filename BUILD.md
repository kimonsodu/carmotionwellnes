# Building & releasing Orbital

Two artifacts ship from this repo:

- **Orbital for Windows** — free desktop overlay → self-contained installer.
- **Orbital Phone (Android)** — subscription app → signed AAB for Google Play.

---

## Windows (desktop overlay)

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) (for the installer) — put `iscc` on PATH.

### Build the release exe + installer
From the repo root:

```powershell
.\publish.ps1 -Installer
```

This:
1. Reads `<Version>` from `OrbitalOverlay.csproj` (single source of truth).
2. Stops any running `OrbitalOverlay` (it locks the exe) and cleans `bin\publish\win-x64`.
3. Publishes a **self-contained, single-file win-x64** exe to
   `bin\publish\win-x64\OrbitalOverlay.exe`.
4. If `iscc` is on PATH, compiles `installer\Orbital.iss` to
   `bin\installer\Orbital-Setup-<version>.exe`.

Run `.\publish.ps1` without `-Installer` to build just the exe. To compile the
installer manually: `iscc /DMyAppVersion=<version> .\installer\Orbital.iss`.
See `installer\README.md` for installer details.

### Dev loop
`dotnet run` from the repo root builds + launches the overlay and control panel.
The single-file/self-contained knobs in the csproj only apply to `dotnet publish`.

> Note: `dotnet build` does **incremental** builds and can silently skip a
> recompile. Use `dotnet build --no-incremental -c Debug` when in doubt.

---

## Android (Orbital Phone)

### Prerequisites
- Android Studio (or the Android SDK + Gradle wrapper in `android/`).

### One-time: create the upload keystore
Release builds are signed from `android/keystore.properties`, which is **gitignored
and must never be committed**. Generate an upload keystore:

```powershell
keytool -genkeypair -v -keystore android\upload-keystore.jks `
  -keyalg RSA -keysize 2048 -validity 10000 -alias orbital-upload
```

Then copy the template and fill it in:

```powershell
Copy-Item android\keystore.properties.example android\keystore.properties
# edit android\keystore.properties: storeFile / storePassword / keyAlias / keyPassword
```

Keep the `.jks` and `keystore.properties` somewhere safe and backed up — losing the
upload key blocks future Play updates.

### Build a release bundle / APK
```powershell
cd android
.\gradlew bundleRelease     # AAB for Google Play  ->  app/build/outputs/bundle/release/
.\gradlew assembleRelease   # APK for sideload      ->  app/build/outputs/apk/release/
```

Release builds use R8 (`minifyEnabled`/`shrinkResources`). If the keystore file is
absent the release build still produces an **unsigned** artifact (CI without secrets).

### Still TODO for the Android subscription
The remote-to-Windows feature is a **paid subscription**, which requires:
- **Google Play Billing** integration + subscription products defined in Play Console.
- A Play Store listing with the privacy policy (`PRIVACY.md`) linked.
- A signed AAB uploaded to a Play track (internal → closed → production).

---

## Versioning
Bump `<Version>` in `OrbitalOverlay.csproj` (Windows) and `versionName` +
`versionCode` in `android/app/build.gradle` (Android), then add a `CHANGELOG.md`
entry and tag the commit `vX.Y.Z`.
