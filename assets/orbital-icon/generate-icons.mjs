#!/usr/bin/env node
/**
 * Orbital icon builder.
 * Turns the SVG masters in ./svg into every launcher/favicon asset for
 * Android, Windows, and the web.
 *
 * Setup:   npm init -y && npm i sharp png-to-ico
 * Run:     node generate-icons.mjs
 * Output:  ./dist/{web,windows,android}
 *
 * Requires Node 18+. `sharp` rasterizes the SVGs; `png-to-ico` packs .ico files.
 */

import sharp from "sharp";
import pngToIco from "png-to-ico";
import { mkdir, writeFile, copyFile, readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const __dirname = dirname(fileURLToPath(import.meta.url));
const SVG = (name) => join(__dirname, "svg", name);
const DIST = join(__dirname, "dist");

// Rasterize an SVG to a square PNG. Oversamples so small sizes stay crisp.
async function png(svgPath, size, outPath) {
  await mkdir(dirname(outPath), { recursive: true });
  await sharp(svgPath, { density: Math.max(384, size * 3) })
    .resize(size, size, { fit: "contain", background: { r: 0, g: 0, b: 0, alpha: 0 } })
    .png()
    .toFile(outPath);
  return outPath;
}

// Build a multi-size .ico from one SVG.
async function ico(svgPath, sizes, outPath) {
  await mkdir(dirname(outPath), { recursive: true });
  const buffers = await Promise.all(
    sizes.map((s) =>
      sharp(svgPath, { density: Math.max(384, s * 3) })
        .resize(s, s, { fit: "contain", background: { r: 0, g: 0, b: 0, alpha: 0 } })
        .png()
        .toBuffer()
    )
  );
  await writeFile(outPath, await pngToIco(buffers));
  return outPath;
}

const ADAPTIVE = { mdpi: 108, hdpi: 162, xhdpi: 216, xxhdpi: 324, xxxhdpi: 432 };
const LEGACY = { mdpi: 48, hdpi: 72, xhdpi: 96, xxhdpi: 144, xxxhdpi: 192 };

async function buildWeb() {
  const dir = join(DIST, "web");
  await png(SVG("favicon.svg"), 16, join(dir, "favicon-16.png"));
  await png(SVG("favicon.svg"), 32, join(dir, "favicon-32.png"));
  await png(SVG("favicon.svg"), 48, join(dir, "favicon-48.png"));
  await ico(SVG("favicon.svg"), [16, 32, 48], join(dir, "favicon.ico"));
  await copyFile(SVG("favicon.svg"), join(dir, "favicon.svg"));
  await png(SVG("orbital-mark.svg"), 180, join(dir, "apple-touch-icon.png"));
  await png(SVG("orbital-mark.svg"), 192, join(dir, "icon-192.png"));
  await png(SVG("orbital-full.svg"), 512, join(dir, "icon-512.png"));
  await png(SVG("orbital-maskable.svg"), 512, join(dir, "icon-512-maskable.png"));
  await copyFile(join(__dirname, "site.webmanifest"), join(dir, "site.webmanifest"));
  console.log("web      -> dist/web");
}

async function buildWindows() {
  const dir = join(DIST, "windows");
  // Rounded tile reads well un-masked in the Windows taskbar.
  await ico(SVG("favicon.svg"), [16, 24, 32, 48, 64, 128, 256], join(dir, "orbital.ico"));
  await png(SVG("favicon.svg"), 256, join(dir, "orbital-256.png"));
  console.log("windows  -> dist/windows/orbital.ico");
}

async function buildAndroid() {
  const root = join(DIST, "android", "res");
  for (const [d, size] of Object.entries(ADAPTIVE)) {
    await png(SVG("orbital-foreground.svg"), size, join(root, `mipmap-${d}`, "ic_launcher_foreground.png"));
    await png(SVG("orbital-monochrome.svg"), size, join(root, `mipmap-${d}`, "ic_launcher_monochrome.png"));
  }
  for (const [d, size] of Object.entries(LEGACY)) {
    await png(SVG("orbital-mark.svg"), size, join(root, `mipmap-${d}`, "ic_launcher.png"));
    await png(SVG("orbital-mark.svg"), size, join(root, `mipmap-${d}`, "ic_launcher_round.png"));
  }
  // Adaptive icon descriptor + background colour.
  const adaptiveXml = `<?xml version="1.0" encoding="utf-8"?>
<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
    <background android:drawable="@color/ic_launcher_background" />
    <foreground android:drawable="@mipmap/ic_launcher_foreground" />
    <monochrome android:drawable="@mipmap/ic_launcher_monochrome" />
</adaptive-icon>
`;
  await mkdir(join(root, "mipmap-anydpi-v26"), { recursive: true });
  await writeFile(join(root, "mipmap-anydpi-v26", "ic_launcher.xml"), adaptiveXml);
  await writeFile(join(root, "mipmap-anydpi-v26", "ic_launcher_round.xml"), adaptiveXml);
  await mkdir(join(root, "values"), { recursive: true });
  await writeFile(
    join(root, "values", "ic_launcher_background.xml"),
    `<?xml version="1.0" encoding="utf-8"?>
<resources>
    <color name="ic_launcher_background">#10151E</color>
</resources>
`
  );
  await png(SVG("orbital-full.svg"), 512, join(DIST, "android", "play_store_512.png"));
  console.log("android  -> dist/android/res  (+ play_store_512.png)");
}

await mkdir(DIST, { recursive: true });
await buildWeb();
await buildWindows();
await buildAndroid();
console.log("\nDone. See dist/. Read README.md for where each file goes.");
