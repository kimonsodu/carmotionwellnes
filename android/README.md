# Orbital Phone (Android sensor streamer)

Streams your phone's accelerometer + gyroscope to the **Orbital** overlay on your laptop, so
laptops without a motion sensor still work — and, unlike the browser page, it **keeps streaming
with the screen off** (it runs as a foreground service).

No internet/cell needed: the phone talks to the laptop over the **local link** (USB tether or a
WiFi hotspot). Data goes out as small UDP packets to the PC's `App (UDP)` address shown in the
Orbital control panel.

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
2. Run **Orbital** on the laptop (its panel shows `Bluetooth — pair this PC "<name>" ...`).
3. In the app: **Bluetooth → Choose paired PC →** pick the laptop **→ Start**.

### WiFi — when both are on one network
Any shared network works: the **same WiFi router**, a **phone/laptop hotspot**, or **USB tether**
(USB works with mobile data off and charges the phone).
1. Run **Orbital**; the panel shows `WiFi — ... <ip> : 8443` and a **QR**.
2. **Scan the QR** with the phone camera — it opens this app with the address pre-filled (no browser,
   no security warning). Or choose **WiFi** and type the address. Then **Start**.

Either way: the phone shows live numbers + Hz, the laptop panel flips to **Phone connected ✓**,
and the **screen can then turn off** — the notification keeps it streaming.

Tune feel on the laptop as usual (Strength / Flip / **Swap ↕↔** / Recenter) to match how the
phone is mounted. Dots react to real *acceleration* (turns, gas, brake) — plus an optional
**Hill / grade** cue (Advanced) that drifts the dots on slopes from the pitch of gravity.

**Stream and show dots at once.** *Where the dots go* is two independent tick-boxes, not a
choice: enable **Stream to laptop**, **Cue on this phone**, or **both**. With both ticked the
phone drives the laptop overlay *and* shows its own dots — start each from its own button.

The **Hill / grade** slider (Advanced) is a separate, signed control: it drifts the dots on a
climb/descent even at a steady speed, independently of Accel/brake. Centre = off; slide left to
reverse the uphill/downhill direction.

## Test it without driving — Simulation

Want to confirm the cue reacts before you hit the road? Tick **Where the dots go → Cue on this
phone**, tap **Start cue overlay**, then open **Advanced → Simulation (test motion)** and pick a
scenario:

- **All scenarios** loops the full script — accelerate, brake, turns, up/down-hill grades, and a
  sideways "train" seating case — so you can watch every axis respond in turn.
- The single scenarios (Accelerate, Brake, Turn left/right, Uphill, Downhill, Sideways) ease in and
  hold so you can study one motion.

Simulation feeds synthetic IMU through the *same* motion pipeline as the real sensors (gravity
removal, gates, smoothing and the cue render are all exercised), and **overrides** the real
accelerometer/gyro while it's on. Set it back to **Off (real sensors)** to return to normal — it
reacts live, no restart needed.

## Notes

- If nothing arrives: re-check the PC address, confirm both devices are on the same
  tether/hotspot, and that **OrbitalOverlay** is allowed through Windows Firewall (it tries to
  add a rule; otherwise allow it when Windows prompts, or once via an elevated run).
- Some phones aggressively kill background apps. If streaming stops with the screen off, exclude
  **Orbital Phone** from battery optimization (Settings → Apps → Orbital Phone → Battery →
  Unrestricted).
- Wire format is plain JSON over UDP, identical to the browser path, so the laptop treats both
  sources the same.
