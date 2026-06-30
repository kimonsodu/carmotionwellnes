# Orbital — overlay motion cues (Windows)

A transparent, always-on-top, **click-through** overlay that floats drifting dots down the
left and right edges of your screen to reduce car sickness while you work. It reads your
laptop's **built-in accelerometer** through Windows' native sensor API on a 2-in-1 like the
HP Spectre x360 — or, on laptops without a sensor, it streams motion from **your phone**
over a local link with no internet or cell signal (see *Use your phone as the sensor* below).

Clicks pass straight through the dots to whatever app is underneath — keep working normally.

## Run it

**Easiest — the installer.** Download `Orbital-Setup.exe` and run it. It's a per-user install
(no admin prompt) and **bundles the .NET runtime**, so nothing else to install. Orbital lands in
Start and in Settings → Apps. First run shows a SmartScreen *"Windows protected your PC"* notice
because the app is new/unsigned — click **More info → Run anyway**.

**From source.** You need the **.NET 8 SDK** (free): https://dotnet.microsoft.com/download —
then from this folder:

```
dotnet run
```

Either way, two windows appear: the full-screen cue overlay, and a small **Orbital** control panel.

> Tip: this is fastest to build, run, and tweak inside **Claude Code** — it runs on your
> machine, so if the first build complains about anything it can fix and re-run immediately.

## Controls (the small panel)

- **Strength** — how strongly the dots react. Start around the middle and push it up.
- **Accel / Brake** — fore/aft cue trim. Sign sets direction (accelerate = dots down); centre is off.
- **Hill / grade** — a *separate* cue for slopes, driven by the pitch of gravity rather than
  acceleration, so the dots drift on a climb/descent even at a steady speed. Independent of
  Accel/Brake and Flip vertical — slide left of centre to reverse the hill direction, centre = off.
- **Dot size** — how big the dots are. Bump it up if they're too subtle to catch in your
  peripheral vision, down if they're distracting.
- **Flip ↕ / Flip ↔** — reverse vertical (accel/brake) / horizontal (turn) direction. The
  accelerometer's mounting orientation varies by machine, so if the dots flow the *wrong* way
  during a turn or when braking, toggle these.
- **Flip hill ⛰** — reverse *only* the hill/grade cue (uphill vs downhill) without touching
  accel/brake, so you can correct one direction independently of the other.
- **Swap ↕↔** — swap which axis drives up/down vs left/right. If gas/brake moves the dots
  *sideways* (or turns move them up/down), the device is rotated relative to the car — flip
  this and it sorts the channels out.
- **Pause** — freeze the dots.
- **Recenter** — re-zero everything after you've settled into your seat. Hold still for a
  moment after pressing it so it re-learns which way is down.
- **Start with Windows** — launch Orbital automatically at sign-in (per-user, no admin). Starts
  with the dots suppressed until there's motion.
- **Quit** — really exit (both windows). The window's **[X]** only hides to the tray.

Strength, dot size, both flips and swap are **remembered between runs** (saved to
`%AppData%\Orbital\settings.json`).

### Test it without driving — Simulation

The **SIMULATION (test)** card (near the bottom of the panel, by Diagnostics) fakes the motion
so you can confirm the cue reacts to every scenario from your desk — no car needed. It feeds
synthetic IMU readings into the *same* pipeline the real sensor uses (gravity removal, axis
auto-learn, auto-hide gate, dot flow), so what you see is what you'd get on the road.

- **Off** (default every launch) — the real sensor drives the dots, exactly as normal.
- **All** — loops through the full script: accelerate (dots down), brake (up), turn left/right,
  uphill/downhill (road grade). The active phase name shows live under the buttons.
- Or pick a **single** scenario (Accel, Brake, Left, Right, Uphill, Downhill) — it drifts the dots
  steadily *one way* for ~9 s, then **stops** (a brief rest, no backward drift), and repeats, so you
  can clearly read the direction. Side seats (facing left/right) mirror each other; hills cue too.
- A **note** on the overlay names the current maneuver + seat (e.g. "Accelerate · facing left"), so
  you always know what the cue is reacting to.
- **Seat orientation** — which way the rider faces (*forward / left / right / rear*), applied to
  *whatever* scenario is running, so you can test accel/turns/hills from any seat. Facing left or
  right shows forward motion (and hills) on the left/right channel; facing rear reverses it.

While a scenario other than Off is selected the simulation **overrides the real sensor**; switch
back to **Off** to return to it. Simulation is a test aid — it's never persisted, so the app
always starts with it Off.

## Global hotkeys

These work from any app, so you don't have to alt-tab to the panel mid-drive:

- **Ctrl+Alt+P** — pause / resume
- **Ctrl+Alt+R** — recenter
- **Ctrl+Alt+[** / **Ctrl+Alt+]** — strength down / up (hold to ramp)
- **Ctrl+Alt+V** / **Ctrl+Alt+H** — flip ↕ / flip ↔

If another app already owns one of these combos, the panel notes which under the hotkey list
and that one simply won't fire.

## System tray

A small **Orbital** icon sits in the tray. Double-click it to show/hide the panel, or
right-click for Show / Pause-Resume / Recenter / Quit. Closing the panel with **[X]** or
**minimizing** it hides it to the tray; the overlay cues keep running. Use **Quit** (button or
tray menu) to actually exit.

## Use your phone as the sensor

Most laptops have no motion sensor. So Orbital can take its motion from a phone mounted in the
car instead — **no internet / no cell signal needed**. The link between phone and laptop is
local radio (or a cable); it works in the woods, in a dead zone, with mobile data off.

The panel's **PHONE SENSOR** section shows both connection paths plus a **QR code**. There are
two ways to stream:

### The Orbital Phone app (recommended)

A small **Android app** (`android/` folder). Unlike the browser page it **keeps streaming with
the screen off** — it runs as a foreground service — so the phone can sleep, mounted and
charging, while it feeds motion to the laptop. Install it once, then pick a transport in the app:

- **Bluetooth** — needs **no network at all**, the simplest setup. Pair the laptop and phone
  once in **Android Settings → Bluetooth** (accept on the PC too). The panel shows
  `Pair this PC ("<name>") …`. Then in the app: **Bluetooth → choose the paired PC → Start**.
- **WiFi** — when both devices share a link: same **WiFi router**, a **phone/laptop hotspot**, or
  **USB tether** (works with mobile data off and charges the phone). The panel shows
  `<ip> : 8443` and a QR. **Scan the QR** with the phone camera — it opens the app with the
  address pre-filled — or type the address in the app. Then **Start**.

To get the app: open the `android/` folder in **Android Studio** and **Run ▶** to your phone
(or **Build → Build APK** and sideload). See `android/README.md` for the one-time build steps.

### Browser fallback (no install)

No app needed — works on any phone, but the **screen must stay on**. Put both devices on the same
local link (WiFi hotspot or USB tether), then **scan the QR / open the link** shown in the panel.
The browser warns the connection is **not secure** — that's the self-signed certificate; choose
**Advanced → proceed anyway**. (Browsers only allow motion sensors over HTTPS, so the overlay
serves one; it's local-only, nothing leaves your devices.) Tap **Start**; on iPhone, allow the
motion-sensor prompt.

### Either way

The page/app shows live numbers when it's streaming and the panel flips to **Phone connected ✓**.
Everything else — Strength, Flip, **Swap ↕↔**, Recenter — works exactly the same with the phone
as the source; use them to match the phone's mounting orientation to the car.

> The phone overrides the laptop sensor while it's streaming, and the laptop takes back over if
> the phone drops. The QR/address updates automatically when a hotspot or tether comes up after
> launch, so you can start Orbital first and connect the phone afterward. All three transports
> (Bluetooth, WiFi app, browser) send the same JSON frames — the laptop treats them identically.

## How the motion works

Two independent channels:

- **Left ↔ right** dots flow with **turns** — driven mainly by **yaw rate about gravity** (the
  gyro), plus the felt lateral g. Because it keys off rotation-about-down, it works in **any
  mount orientation** (phone flat, upright, or on its side) and you can test it at a desk by
  yawing the device — no real sideways g needed.
- **Up ↕ down** dots flow with **fore/aft** acceleration: **accelerating** sends them
  **down**, **braking** sends them **up** (flip ↕ if reversed, or **Swap ↕↔** if gas/brake
  ends up on the *sideways* channel instead — see below).

The dots' *velocity* tracks the car's acceleration through a leaky integrator, so during a
**sustained turn they keep streaming the whole time** and only settle when the motion stops —
matching what your inner ear feels. Two things make that hold up in the car:

- Gravity is filtered out, but the filter is **frozen while real motion is present** and only
  re-learns when the device is near rest. Otherwise a steady curve's constant sideways pull
  gets absorbed into the "gravity" estimate within half a second and the dots stop mid-turn.
- The resting **orientation is locked once the device is near rest** (re-armed by Recenter), so
  a hard turn can't shift the gravity estimate across an axis boundary and leak sideways motion
  into the up/down channel. It works whether the laptop is flat or propped up.

An accelerometer alone can't tell which way the laptop is *facing*, only which way is down. So
if it's rotated relative to the car, forward/back acceleration can land on the left/right
channel (or vice-versa). **Swap ↕↔** exchanges the two channels to fix that. A quick desk test:
slide the laptop **forward/back** on the table — the dots should move **up/down**; slide it
**left/right** — they should move **sideways**. If those are crossed, toggle Swap.

## Notes / things to tweak

- If the dots don't move at all, the panel will say no sensor was found — either confirm
  Windows shows the accelerometer under Settings → Privacy → Motion, or use the phone instead
  (see *Use your phone as the sensor*).
- Phone won't connect over **WiFi/browser**? Windows Firewall may be blocking the port — allow
  **OrbitalOverlay** through the firewall when prompted (or once via an elevated run). Confirm both
  devices are on the same hotspot/tether, and (browser only) that you tapped *proceed anyway* past
  the certificate warning.
- **Bluetooth** won't connect? Make sure the PC has a Bluetooth radio and the two are **paired**
  in Windows/Android settings first; then pick the PC in the app.
- App stops streaming with the **screen off**? Some phones aggressively kill background apps —
  exclude **Orbital Phone** from battery optimization (Settings → Apps → Orbital Phone → Battery →
  Unrestricted).
- Tuning constants live near the top of `OverlayWindow` in `Program.cs`
  (`Sens`, `decay`, `gain`, `vmax`, dead-zone `dz`) and match the web prototype, so any feel
  you dialed in there transfers directly.
- The dot strips are 22% of the screen width on each side; change `bandW` to make them
  wider/narrower or `nNear`/`nFar` for density.
