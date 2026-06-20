# Steady Phone (Android sensor streamer)

Streams your phone's accelerometer + gyroscope to the **Steady** overlay on your laptop, so
laptops without a motion sensor still work — and, unlike the browser page, it **keeps streaming
with the screen off** (it runs as a foreground service).

No internet/cell needed: the phone talks to the laptop over the **local link** (USB tether or a
WiFi hotspot). Data goes out as small UDP packets to the PC's `App (UDP)` address shown in the
Steady control panel.

## Build it (one time)

1. Install **Android Studio** (you have it).
2. **File → Open** this `android/` folder. Let Gradle sync finish (first sync downloads the
   Gradle distribution + SDK bits; needs internet *once*, on the PC, not in the car).
   - If it prompts to **upgrade the Android Gradle Plugin / Gradle**, accept it.
   - If it complains the SDK/`local.properties` is missing, click the offered fix (it writes
     your SDK path automatically).
3. Plug the phone in (USB), enable **Developer options → USB debugging**, and click **Run ▶**
   in Android Studio to install + launch the app. (Or **Build → Build APK** and copy the APK to
   the phone to sideload.)

## Use it

1. On the laptop, run **Steady**. The control panel's **PHONE SENSOR** section shows
   `App (UDP):  <ip> : 8443`.
2. Connect the phone to the laptop's local link:
   - **USB tether** (most reliable): plug in, enable **USB tethering**. Works with mobile data
     off; charges the phone too.
   - or **WiFi hotspot**: phone hotspot on, laptop joins it (or vice-versa).
3. Open **Steady Phone** on the phone. Type the **PC address** + **port** (`8443`) from the
   panel. Tap **Start streaming**.
4. The phone shows live numbers + Hz; the laptop panel flips to **Phone connected ✓**. Now the
   **screen can turn off** — the notification keeps it streaming.

Tune feel on the laptop as usual (Strength / Flip / **Swap ↕↔** / Recenter) to match how the
phone is mounted. Dots react to real *acceleration* (turns, gas, brake), not tilt.

## Notes

- If nothing arrives: re-check the PC address, confirm both devices are on the same
  tether/hotspot, and that **SteadyOverlay** is allowed through Windows Firewall (it tries to
  add a rule; otherwise allow it when Windows prompts, or once via an elevated run).
- Some phones aggressively kill background apps. If streaming stops with the screen off, exclude
  **Steady Phone** from battery optimization (Settings → Apps → Steady Phone → Battery →
  Unrestricted).
- Wire format is plain JSON over UDP, identical to the browser path, so the laptop treats both
  sources the same.
