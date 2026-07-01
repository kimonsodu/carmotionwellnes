# Orbital — Privacy Policy

_Last updated: 2026-06-30_

Orbital is a motion-sickness comfort overlay. It exists to read device motion and
draw cues. **It does not collect, store, or transmit your personal data to the
Author or any third party.** There is no account, no analytics, no ad SDK, and no
cloud server.

## What the apps access, and why

### Orbital for Windows (desktop)
- **Motion sensors** (accelerometer/gyro, where present) — read locally to drive
  the on-screen cue. Never leaves your PC.
- **Local network sockets** — only to receive motion frames from your own phone
  on the **same local link** (WiFi hotspot, USB tether, or Bluetooth). Traffic is
  device-to-device and local-only; nothing is sent over the internet.
- **Settings** are saved locally to `%AppData%\Orbital\settings.json`. A
  self-signed TLS certificate for the local link is cached under the same folder.

### Orbital Phone (Android)
Permissions requested and the reason for each:

| Permission | Why |
|---|---|
| Body/motion sensors (accelerometer, gyroscope) | Core function: measure vehicle motion to drive the cue. |
| `BLUETOOTH` / `BLUETOOTH_CONNECT` | Stream motion to your paired laptop over Bluetooth (no network needed). |
| `INTERNET` (local sockets) | Stream motion to your laptop over WiFi/USB tether on your **local** network. Not used to reach the internet. |
| `ACCESS_FINE_LOCATION` / `ACCESS_COARSE_LOCATION` | Optional in-vehicle gate via the system location provider. Degrades gracefully if denied; can be declined. |
| `FOREGROUND_SERVICE` (+ data-sync/location/special-use) | Keep streaming and/or showing the cue with the screen off, via a persistent, user-stoppable notification. |
| `SYSTEM_ALERT_WINDOW` | Draw the click-through cue overlay on top of other apps in phone-cue mode. |
| `POST_NOTIFICATIONS` | Show the persistent control notification for the foreground service. |
| `WAKE_LOCK` | Keep the sensor stream alive while the screen is off. |
| `com.android.vending.BILLING` | Purchase/verify the remote-streaming subscription via Google Play. Payment is handled entirely by Google; no payment data reaches the app. |

### Data handling
- **Sensor and motion data** are processed in real time on-device and sent only to
  your own paired computer over a local link. They are **not** recorded, retained,
  or uploaded.
- **Location** (if granted) is used only as an on-device gate and is not stored or
  transmitted.
- Orbital contains **no third-party trackers or advertising**.

### Subscriptions (Android)
The Android app's remote-to-Windows streaming is a paid subscription handled by the
**Google Play Store**. Billing, payment details, and purchase history are managed by
Google under Google's Privacy Policy — the Author never receives your payment
information.

### Children
Orbital is not directed at children and collects no personal information from anyone.

### Contact
Questions about this policy: kodu.simon@gmail.com
