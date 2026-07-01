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

### The Android subscription (remote streaming to Windows)
The on-phone cue overlay and the Windows app are **free**. Streaming the phone's motion to the
Windows app (the `SensorService` "Start streaming" flow) is a **paid subscription**, enforced by
**Google Play Billing** (already integrated — `BillingManager.kt` / `Entitlements.kt`; the paywall
lives in `MainActivity.showPaywall()`). Entitlement is client-side (no backend): Play's local cache
is mirrored to prefs with a 14-day offline grace so a paid rider keeps streaming with no signal.

**In Play Console, before this earns:**
1. Create a **subscription** with product id **`orbital_remote`** (must match
   `BillingManager.SUB_PRODUCT_ID`). Add the **base plans** you want (e.g. `monthly`, `yearly`) and
   any intro/free-trial **offers** — the paywall lists whatever base plans/offers you configure and
   shows their live localized prices. No code change to add/rename plans.
2. Upload a signed AAB to a **track** and add **license testers** — billing only works for the app
   installed **from Play** (internal-testing track is fine); a locally-sideloaded APK returns
   "billing unavailable" and the paywall shows its Retry state.
3. Link the privacy policy (`PRIVACY.md`) in the store listing; roll internal → closed → production.

> Client-side entitlement can be defeated on a rooted/patched device — acceptable for this low-stakes
> sub with no backend. If that changes, verify purchase tokens server-side via the Play Developer API.

---

## Versioning
Bump `<Version>` in `OrbitalOverlay.csproj` (Windows) and `versionName` +
`versionCode` in `android/app/build.gradle` (Android), then add a `CHANGELOG.md`
entry and tag the commit `vX.Y.Z`.
