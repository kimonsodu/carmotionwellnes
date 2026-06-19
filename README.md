# Steady — overlay motion cues (Windows)

A transparent, always-on-top, **click-through** overlay that floats drifting dots down the
left and right edges of your screen to reduce car sickness while you work. It reads your
laptop's **built-in accelerometer** through Windows' native sensor API (no phone, no browser
permission dance), so it works on a 2-in-1 like the HP Spectre x360.

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
- **Flip ↕ / Flip ↔** — reverse vertical / horizontal direction. The accelerometer's mounting
  orientation varies by machine, so if the dots flow the *wrong* way during a turn or when
  braking, toggle these.
- **Pause** — freeze the dots.
- **Recenter** — re-zero everything after you've settled into your seat.
- **Quit** — close both windows.

Strength and both flips are **remembered between runs** (saved to
`%AppData%\Steady\settings.json`).

## Global hotkeys

These work from any app, so you don't have to alt-tab to the panel mid-drive:

- **Ctrl+Alt+S** — pause / resume
- **Ctrl+Alt+R** — recenter
- **Ctrl+Alt+[** / **Ctrl+Alt+]** — strength down / up (hold to ramp)
- **Ctrl+Alt+V** / **Ctrl+Alt+H** — flip ↕ / flip ↔

## System tray

A small **Steady** icon sits in the tray. Double-click it to show/hide the panel, or
right-click for Show / Pause-Resume / Recenter / Quit. **Minimizing** the panel hides it
to the tray; the overlay cues keep running.

## How the motion works

Two independent channels:

- **Left ↔ right** dots flow with **lateral** acceleration — i.e. **turns**.
- **Up ↕ down** dots flow with **fore/aft** acceleration: **accelerating** sends them
  **down**, **braking** sends them **up** (flip ↕ if reversed on your mounting).

The dots' *velocity* tracks the car's acceleration through a leaky integrator, so during a
**sustained turn they keep streaming the whole time** and only settle when the motion stops —
matching what your inner ear feels. Two things make that hold up in the car:

- Gravity is filtered out, but the filter is **frozen while real motion is present** and only
  re-learns when the device is near rest. Otherwise a steady curve's constant sideways pull
  gets absorbed into the "gravity" estimate within half a second and the dots stop mid-turn.
- The resting **orientation is detected once and locked** (re-armed by Recenter), so a hard
  turn can't shift the gravity estimate across an axis boundary and leak sideways motion into
  the up/down channel. It works whether the laptop is flat or propped up.

## Notes / things to tweak

- If the dots don't move at all, the panel will say no accelerometer was found. Confirm
  you're on the 2-in-1 and that Windows shows it under Settings → Privacy → Motion.
- Tuning constants live near the top of `OverlayWindow` in `Program.cs`
  (`Sens`, `decay`, `gain`, `vmax`, dead-zone `dz`) and match the web prototype, so any feel
  you dialed in there transfers directly.
- The dot strips are 22% of the screen width on each side; change `bandW` to make them
  wider/narrower or `nNear`/`nFar` for density.
