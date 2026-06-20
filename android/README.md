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

Pick a **Connection** in the app: **Bluetooth** (default) or **WiFi**.

### Bluetooth — recommended, needs no network at all
Works with the phone fully offline and the laptop on any/no network.
1. **Pair** the laptop and phone once in **Android Settings → Bluetooth** (accept on the PC too).
2. Run **Steady** on the laptop (its panel shows `Bluetooth — pair this PC "<name>" ...`).
3. In the app: **Bluetooth → Choose paired PC →** pick the laptop **→ Start**.

### WiFi — when both are on one network
Any shared network works: the **same WiFi router**, a **phone/laptop hotspot**, or **USB tether**
(USB works with mobile data off and charges the phone).
1. Run **Steady**; the panel shows `WiFi — ... <ip> : 8443` and a **QR**.
2. **Scan the QR** with the phone camera — it opens this app with the address pre-filled (no browser,
   no security warning). Or choose **WiFi** and type the address. Then **Start**.

Either way: the phone shows live numbers + Hz, the laptop panel flips to **Phone connected ✓**,
and the **screen can then turn off** — the notification keeps it streaming.

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
