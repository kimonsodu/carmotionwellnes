# Orbital — app launcher & favicon kit

Source SVGs + a build script that generates every launcher/favicon asset for
**Android**, **Windows**, and the **web** from one set of masters.

Brand: dark "instrument" theme — teal motion-field dots at the edges, one amber
*level point* at center.

| Token | Hex | Role |
|---|---|---|
| ink | `#10151E` | background / tile |
| phosphor | `#6FD8C6` | the dots (teal field) |
| signal | `#E0A24A` | the center level point (amber) |

> **Family option:** to tie Orbital's warm accent to the Wroughtery Ember, set
> `signal` to `#C24E2A` in every `svg/*.svg` (replace `#E0A24A`) and rerun the
> build. Kept warmer (`#E0A24A`) here because it reads better on the teal field.

---

## Build

Requires Node 18+.

```bash
npm init -y
npm install sharp png-to-ico
node generate-icons.mjs
```

Outputs land in `./dist/{web,windows,android}`.

> If `npm install` errors with `ENOSYS: symlink` (some mounted/synced folders
> can't make symlinks), run `npm install --no-bin-links`, or build in a normal
> local directory and copy `dist/` where you need it.

To restyle, edit the masters in `svg/` and rerun — every size regenerates.

---

## SVG masters (in `svg/`)

| File | Canvas | Used for |
|---|---|---|
| `orbital-full.svg` | 512, full-bleed | Play Store 512, hi-res / detailed icon |
| `orbital-mark.svg` | 512, full-bleed | small launcher sizes, apple-touch, PWA 192 |
| `orbital-maskable.svg` | 512, safe-zone | PWA maskable icon |
| `orbital-foreground.svg` | 432, transparent | Android adaptive foreground |
| `orbital-monochrome.svg` | 432, white | Android 13+ themed icon |
| `favicon.svg` | 512, rounded | scalable browser favicon + Windows `.ico` |

Rule of thumb: **detailed** master for 128px and up, **simplified mark** for
anything smaller.

---

## Where each output goes

### Web  (`dist/web/` → your site root or `/public`)
Files: `favicon.ico`, `favicon.svg`, `favicon-16/32/48.png`,
`apple-touch-icon.png`, `icon-192.png`, `icon-512.png`,
`icon-512-maskable.png`, `site.webmanifest`.

Add to `<head>`:

```html
<link rel="icon" href="/favicon.ico" sizes="any">
<link rel="icon" href="/favicon.svg" type="image/svg+xml">
<link rel="apple-touch-icon" href="/apple-touch-icon.png">
<link rel="manifest" href="/site.webmanifest">
<meta name="theme-color" content="#10151E">
```

### Windows  (`dist/windows/orbital.ico`)
Multi-size `.ico` (16→256). For a .NET / WPF app, in the `.csproj`:

```xml
<PropertyGroup>
  <ApplicationIcon>orbital.ico</ApplicationIcon>
</PropertyGroup>
```

And the window chrome (WPF): `<Window ... Icon="orbital.ico">`.

### Android  (`dist/android/res/` → `app/src/main/res/`)
Copy the `res/` tree in (it merges with yours): `mipmap-*` foreground /
monochrome / legacy icons, `mipmap-anydpi-v26/ic_launcher.xml`, and
`values/ic_launcher_background.xml`.

In `AndroidManifest.xml`:

```xml
<application
    android:icon="@mipmap/ic_launcher"
    android:roundIcon="@mipmap/ic_launcher_round" ... >
```

Use `dist/android/play_store_512.png` for the Play Store listing icon.

---

## Notes
- All raster output is 32-bit RGBA PNG; `.ico`s are real multi-size icons.
- The amber center point is the brand signature and the one bold move — if you
  ever want a calmer mark, delete the amber `<circle>` from the SVGs and rerun.
- Android background is a flat color (`#10151E`) via XML, so there's no
  background PNG to ship per density.
