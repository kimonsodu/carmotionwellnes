# Changelog

All notable changes to Orbital are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/); versions follow the app's
`<Version>` (Windows) / `versionName` (Android).

## [Unreleased]

### Added
- **Android subscription** — remote streaming to the Windows app is now gated behind a Google Play
  subscription (`orbital_remote`) via Play Billing v7. The on-phone cue overlay and the Windows app
  stay free. Client-side entitlement with a 14-day offline grace so a paid rider keeps streaming
  without a signal. Paywall lists the base plans/offers configured in Play Console.

## [1.0.0] — 2026-06-30

First public release.

### Added
- **Windows overlay** — transparent, always-on-top, click-through dot cue that
  reads the laptop's built-in accelerometer (Windows sensor API) and drifts
  peripheral dots with vehicle motion to reduce car sickness.
- **Phone-as-sensor** — stream motion from an Android phone over **Bluetooth**,
  **WiFi/USB tether**, or a **browser** fallback; all local-only, no internet.
- **Orbital Phone (Android)** — foreground-service sensor streamer that keeps
  running with the screen off; can also draw its own on-phone cue overlay.
- **Cue styles** — Dots, Streaks, Rails, Horizon, Flow, Chevrons.
- **Placement** — side strips or full peripheral frame (top/bottom bands).
- **Independent hill/grade cue** driven by the pitch of gravity.
- **Simulation mode** to test every maneuver from a desk, with seat orientation.
- Global hotkeys, system-tray control, start-with-Windows, per-user installer.

### Fixed
- Grade phantom cue lingering after accel/stop (faster grade baseline).
- Held screen-tilt no longer reads as a never-ending hill (gyro tilt-rate rebase).
- Dots slide in/out at screen edges (bleed/fade renderer) instead of popping.
- Placement toggle on/off now matches what renders.
- Streaks no longer pop into larger static dots when motion stops with auto-hide off.

### Known limitations
- Windows build is **x64 only**, requires **Windows 10 build 19041+** or Windows 11.
- The installer is unsigned, so first run shows a SmartScreen notice
  (More info → Run anyway).
