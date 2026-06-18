# Templates

ViewOwl templates are self-contained HTML files. A `<body>` element with `data-vow-*` attributes tells the system how to capture and deliver the template to devices.

---

## The three classes

### Class A — Full-colour server grab

The server opens the URL in headless Chromium, takes a screenshot, converts to BGR565, and pushes to devices. No constraints on HTML/CSS — gradients, images, shadows, web fonts, external data fetches all work.

**Use when:** you need rich visuals, live external data (weather APIs, stock tickers), or anything that requires a real browser to render correctly.

**Device sees:** one static BGR565 frame, refreshed every `data-vow-refresh` minutes.

> **External-API templates** render the same way — but on a shared instance a *browser-side preview* sends the visitor's IP to the third-party API (a GDPR consideration), so the hosted demo keeps them out of its served set as examples under `ExchangeFolder/External/`. Rendering for a device is server-side and does not expose a visitor IP. Details and the planned data-source proxy: [Class A → External data](class-a.md).

---

### Class B — Canvas push

A canvas-based template that can be grabbed by the server **or** pushed live from the admin panel browser — bypassing Chrome entirely. The admin panel renders the template in a hidden iframe, reads the canvas pixels, converts to BGR565, and POSTs directly to the device.

**Use when:** you want a live push loop (1–5 s updates), or you're building a widget that works well with a limited palette.

**Constraints:**
- Canvas only — no DOM elements in the rendered output
- No `Math.random()` or non-deterministic values if you want CRC dedup to work
- Limited effective palette after BGR565 quantisation (~12 safe shades per hue)
- No gradients, alpha, or shadows — these produce colour banding on the display
- Canvas size must follow `window.innerWidth / window.innerHeight` — never hardcode `480` or `320`

**Device sees:** one static frame (server grab) or continuously updated frames (browser push loop).

---

### Class C — Multi-frame animation

The server renders each frame individually by appending `?vow_frame=N` to the URL. All N frames are transferred to the device as a batch and stored in the ESP32's flash partition. The device plays them back in a loop at `data-vow-fps` frames per second — no server connection needed during playback.

**Use when:** you want smooth looping animations on the device that run independently of WiFi.

**Constraints:**
- `?vow_frame=N` must be the only source of variation between frames — same frame index must always produce the same pixels
- **`Math.random()` is forbidden** — breaks CRC dedup and causes infinite re-renders. Use `Math.sin(N)`, `Math.cos(N)`, or a seeded PRNG
- Maximum 16 frames (clamped by the server)
- `data-vow-refresh="0"` for purely decorative animations (never auto-re-grab)

**Device sees:** N-frame looping animation stored in flash, playing at `data-vow-fps` FPS.

---

## data-vow-* attribute contract

All attributes go on the `<body>` element. The server parser is [`TemplateMetadataParser.cs`](../../ViewOwl.Grabber.Engine/TemplateMetadataParser.cs); the frontend mirror is [`vowMeta.js`](../../ViewOwl.Grabber.WebAPI/ClientApp/src/utils/vowMeta.js).

```html
<body
  data-vow-class="C"
  data-vow-name="My Template"
  data-vow-description="Short description shown in the gallery"
  data-vow-category="scifi"
  data-vow-frames="12"
  data-vow-fps="3"
  data-vow-refresh="0"
>
```

| Attribute | Type | Default | Notes |
|---|---|---|---|
| `data-vow-class` | `A` / `B` / `C` | `A` | Determines grab strategy |
| `data-vow-name` | string | `""` | Display name in gallery |
| `data-vow-description` | string | `""` | Gallery subtitle |
| `data-vow-category` | string | `other` | See categories below |
| `data-vow-frames` | int | `1` | Class C only — clamped to [1..16] |
| `data-vow-fps` | int | `10` | Class C only — clamped to [1..30] |
| `data-vow-refresh` | int (minutes) | `5` | 0 = never auto-grab |

**Important:** metadata is **not stored in the database**. The `Templates` table stores only `Id`, `Name`, `HtmlContent`, and `CreatedAt`. All `data-vow-*` values are parsed from `HtmlContent` on every read. Editing the HTML is the only way to change metadata — the gallery reflects changes immediately.

### Categories

`weather` · `clock` · `rates` · `menu` · `system` · `scifi` · `ambient` · `other`

To add a new category, edit `VOW_CATEGORIES` in [`vowMeta.js`](../../ViewOwl.Grabber.WebAPI/ClientApp/src/utils/vowMeta.js).

---

## How grabs work

### Class A and B (single frame)

```
DeviceTemplateRefreshWorker
  → Chrome.AddUrlAsync(templateUrl)      opens tab
  → Chrome.TakeScreenshotAsync()         PNG at device resolution
  → ImageSharp resize → BGR565 encode
  → ExchangeFolder/{guid}.bin
```

The worker runs on a configurable cycle (default 5 minutes per template per connected device resolution).

### Class C (multi-frame batch)

```
DeviceTemplateRefreshWorker
  → for N in [0 .. frames-1]:
      Chrome navigates to templateUrl?vow_frame=N
      PNG screenshot → BGR565 → compress
  → writes ExchangeFolder/{guid}_batch.json  (manifest: frame count, fps, batch CRC)
  → UDP Server reads manifest on next HELLO
  → sends BATCH_START → N × DATA → BATCH_COMMIT to device
  → ESP32 writes all frames to flash partition atomically
```

**Skip optimisation:** Before rendering, the worker computes a SHA256 of the template HTML and compares it to a stored sidecar hash (`t{id}_{w}x{h}.htmlhash`). If the hash and the batch CRC both match, the render is skipped entirely.

---

## Multi-resolution support

The server grabs each template at every resolution used by connected devices. A 480×320 device and a 320×240 device each get their own `.bin` file. The grab uses `SetViewportAsync(width, height)` followed by a page reload so JS layout recalculates at the correct size.

Templates should use `window.innerWidth` / `window.innerHeight` rather than hardcoded dimensions. See [Class B](class-b.md) and [Class C](class-c.md) for canvas sizing patterns.

---

## Class-specific guides

- [Class A](class-a.md) — full-colour server grabs
- [Class B](class-b.md) — canvas push templates
- [Class C](class-c.md) — multi-frame animations, wireframe recipe

---

## Included templates

ViewOwl ships ready-made templates in two places:

- **`ExchangeFolder/SitesTemplates/`** — the curated set the hosted demo serves (sci-fi, ambient, clock, system, and other self-contained templates).
- **`ExchangeFolder/External/`** — external-API examples (weather, rates, maps, transit) kept *off* the hosted demo for the privacy reason above; copy one into `SitesTemplates/` on your own instance to use it. See [Class A → External data](class-a.md).

The list below is illustrative — the canonical, machine-readable catalog is `ExchangeFolder/SitesTemplates/index.json`. Some entries below (Crypto, FX Terminal, Trains, the weather templates) now live under `External/`.

### Ambient

| Name | File | Class |
|---|---|---|
| Ambient | ambient.html | A |
| Ambient Aura | ambient-aura.html | A |
| Avia | avia.html | C |
| Avia light | avia-light.html | C |
| Neon | neon.html | A |
| Ocean | ocean.html | A |
| Promo | promo.html | A |
| Pulse | pulse.html | A |
| STATUS — PORT 7 | status-port-7.html | A |
| Storm | storm.html | A |

### Clock

| Name | File | Class |
|---|---|---|
| 4 Clock | 4-clock.html | A |
| Clock | clock.html | A |
| Retro Clock | retro-clock.html | A |
| Retro Meteo | retro-meteo.html | A |

### Rates

| Name | File | Class |
|---|---|---|
| Crypto | crypto.html | A |
| Finance | finance.html | A |
| FX Terminal | fx-terminal.html | A |

### Transport

| Name | File | Class |
|---|---|---|
| Departures | departures.html | A |
| Trains | trains.html | A |

### Weather

| Name | File | Class |
|---|---|---|
| Atmospheric Scanner | atmospheric-scanner.html | C |
| HYD Sys Panel | hyd-sys-panel-light-bule.html | B |
| Orbital Weather Dossier | orbital-weather-dossier.html | A |
| Prism | prism.html | A |
| Weather Canvas | weather-canvas.html | A |
| Weather Matrix | weather-matrix.html | B |

### System

| Name | File | Class |
|---|---|---|
| Gauge | gauge.html | A |

### Sci-Fi

| Name | File | Class |
|---|---|---|
| Airlock | airlock.html | B |
| Airlock A-7 Cycle | airlock-a-7-cycle.html | C |
| Alien Nostromo Terminal v2 | alien-nostromo-terminal-v2.html | C |
| Alien OVERMONITORING type B | alien-overmonitoring-type-b.html | B |
| Alien OVERMONITORING type C | alient-overmonitoring-broken-type-c.html | C |
| Cheyenne Descent | cheyenne-descent.html | C |
| Cryo Vitals Monitor | cryo-vitals-monitor.html | C |
| Hercules Terminal | hercules-terminal.html | C |
| HUD | hud.html | A |
| Life Systems | life-systems.html | A |
| LV-426 Meltdown | lv-426-meltdown.html | C |
| M41A Pulse Rifle | m41a-pulse-rifle.html | C |
| Matrix | matrix.html | A |
| Motion Tracker M314 | motion-tracker-m314.html | C |
| Motion Tracker MK-II | motion-tracker-mk-ii.html | C |
| Narcissus Eject | narcissus-eject.html | C |
| Nostromo Boot | nostromo-boot.html | C |
| Radar | radar.html | A |
| Reactor | reactor.html | A |
| Reactor Core | reactor-core.html | C |
| Sectors | sectors.html | A |
| Space | space.html | A |
| Spaceship Drawing | spaceship-drawing.html | C |
| Star Map | star-map.html | A |
| Sulaco Tactical Scan | sulaco-tactical-scan.html | C |
| UA-571 Sentry Gun | ua-571-sentry-gun.html | C |
| Undock Vector | undock-vector.html | C |
