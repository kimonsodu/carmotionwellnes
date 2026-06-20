# Steady — overlay motion cues (Windows)

A transparent, always-on-top, **click-through** overlay that floats drifting dots down the
left and right edges of your screen to reduce car sickness while you work. It reads your
laptop's **built-in accelerometer** through Windows' native sensor API on a 2-in-1 like the
HP Spectre x360 — or, on laptops without a sensor, it streams motion from **your phone**
(see *Use your phone as the sensor* below).

Clicks pass straight through the dots to whatever app is underneath — keep working normally.

## Run it

You need the **.NET 8 SDK** (free): https://dotnet.microsoft.com/download

Then, from this folder:

```
dotnet run
```

Two windows appear: the full-screen cue overlay, and a small **Steady** control panel.

> Tip: this is fastest to build, run, and tweak inside **Claude Code** — it runs on your
> machine, so if the first build complains about anything it can fix and re-run immediately.

## Controls (the small panel)

- **Strength** — how strongly the dots react. Start around the middle and push it up.
- **Dot size** — how big the dots are. Bump it up if they're too subtle to catch in your
  peripheral vision, down if they're distracting.
- **Flip ↕ / Flip ↔** — reverse vertical / horizontal direction. The accelerometer's mounting
  orientation varies by machine, so if the dots flow the *wrong* way during a turn or when
  braking, toggle these.
- **Swap ↕↔** — swap which axis drives up/down vs left/right. If gas/brake moves the dots
  *sideways* (or turns move them up/down), the device is rotated relative to the car — flip
  this and it sorts the channels out.
- **Pause** — freeze the dots.
- **Recenter** — re-zero everything after you've settled into your seat. Hold still for a
  moment after pressing it so it re-learns which way is down.
- **Quit** — really exit (both windows). The window's **[X]** only hides to the tray.

Strength, dot size, both flips and swap are **remembered between runs** (saved to
`%AppData%\Steady\settings.json`).

## Global hotkeys

These work from any app, so you don't have to alt-tab to the panel mid-drive:

- **Ctrl+Alt+P** — pause / resume
- **Ctrl+Alt+R** — recenter
- **Ctrl+Alt+[** / **Ctrl+Alt+]** — strength down / up (hold to ramp)
- **Ctrl+Alt+V** / **Ctrl+Alt+H** — flip ↕ / flip ↔

If another app already owns one of these combos, the panel notes which under the hotkey list
and that one simply won't fire.

## System tray

A small **Steady** icon sits in the tray. Double-click it to show/hide the panel, or
right-click for Show / Pause-Resume / Recenter / Quit. Closing the panel with **[X]** or
**minimizing** it hides it to the tray; the overlay cues keep running. Use **Quit** (button or
tray menu) to actually exit.

## Use your phone as the sensor

Most laptops have no motion sensor. So Steady can take its motion from a phone mounted in the
car instead — no app to install, and **no internet / no cell signal needed**. The link between
phone and laptop is local radio (or a cable); it works in the woods, in a dead zone, with
mobile data off.

The panel has a **PHONE SENSOR** section with a link and a **QR code**.

1. **Put the phone and laptop on the same local link** (either one):
   - **WiFi hotspot** — turn on the phone's hotspot and join it from the laptop (or vice-versa).
     The hotspot is just a local radio between the two devices; it needs no internet to share.
   - **USB tether** — plug the phone into the laptop and enable USB tethering. Works with mobile
     data **off**, it's rock-solid, and the phone charges at the same time. (Most reliable.)
2. **Scan the QR code** with the phone's camera (or type the link into its browser).
3. The browser will warn about the connection being **not secure** — that's the self-signed
   certificate. Choose **Advanced → proceed anyway**. (Browsers only allow motion sensors over
   HTTPS, so the overlay serves one; it's local-only, nothing leaves your devices.)
4. Tap **Start**. On iPhone, allow the motion-sensor prompt. The page shows live numbers when
   it's streaming, and the panel flips to **Phone connected ✓**.

Keep the phone screen on (it stays awake on its own while the page is open) and leave it mounted
and charging. Everything below — Strength, Flip, **Swap ↕↔**, Recenter — works exactly the same
with the phone as the source; use them to match the phone's mounting orientation to the car.

> The phone overrides the laptop sensor while it's streaming and the laptop takes back over if
> the phone drops. The QR/link updates automatically when you bring a hotspot or tether up after
> launch, so you can start Steady first and connect the phone afterward.

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
- Phone won't connect? Windows Firewall may be blocking the port — allow **SteadyOverlay**
  through the firewall when prompted (or once via an elevated run). Confirm both devices are on
  the same hotspot/tether, and that you tapped *proceed anyway* past the certificate warning.
- Tuning constants live near the top of `OverlayWindow` in `Program.cs`
  (`Sens`, `decay`, `gain`, `vmax`, dead-zone `dz`) and match the web prototype, so any feel
  you dialed in there transfers directly.
- The dot strips are 22% of the screen width on each side; change `bandW` to make them
  wider/narrower or `nNear`/`nFar` for density.
