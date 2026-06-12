# Renderer — Chromium → BGR565 Pipeline

The ViewOwl render pipeline turns any HTML page into a raw BGR565 bitmap ready for direct SPI transmission to an LCD. A full-featured browser — headless Chromium — does the rendering, which means the display supports everything a modern browser supports: CSS animations, web fonts, canvas 2D, fetch(), external APIs, shadows, gradients, WebGL.

This is the part of the project that surprised most people: a $5 microcontroller displaying live, browser-rendered content updated from the internet.

---

## The pipeline

```
HTML (from DB or URL)
        │
        ▼
SiteTemplateController          ← serves HTML over HTTP
GET /SiteTemplate?name=t{id}.html   (Chromium on ARM64 cannot open file://)
        │
        ▼
Chrome.AddUrlAsync()            ← opens a new tab, navigates, waits for networkidle0
        │
        ▼
Chrome.TakeScreenshotAsync()
  │
  ├─ SetViewportAsync(width, height)    ← exact device resolution
  ├─ page.ReloadAsync(Networkidle0)     ← JS re-reads window.innerWidth after resize
  ├─ ScreenshotDataAsync()             ← PNG bytes from Chromium
  ├─ Image.Load<Rgb24>(pngBytes)       ← ImageSharp decode
  ├─ image.Mutate(op => op.Resize())   ← resize if viewport ≠ target (Class A fallback)
  ├─ converter.Convert(image)           ← registered IPixelConverter → byte[]
  │     (default: BGR565 + palette+RLE or palette+LZ4)
  ├─ CRC32 check → skip write if unchanged
  ├─ File.WriteAllBytesAsync({guid}.bin)   ← atomic write via .tmp → rename
  └─ image.SaveAsPng({guid}.png)           ← preview for gallery thumbnails
        │
        ▼
ExchangeFolder/{guid}.bin   ← UDP Server reads this on next HELLO
```

---

## Key source files

| File | Responsibility |
|---|---|
| [`Chrome.cs`](../../ViewOwl.Grabber.Engine/Chrome.cs) | All browser interaction: tab management, screenshot, BGR565 conversion, CRC dedup, .bin/.png write |
| [`TemplateMetadataParser.cs`](../../ViewOwl.Grabber.Engine/TemplateMetadataParser.cs) | Parses `data-vow-*` attributes from HTML body element |
| [`SiteTemplateController.cs`](../../ViewOwl.Grabber.WebAPI/Controllers/SiteTemplateController.cs) | Serves HTML templates to Chromium over HTTP; also serves PNG previews |
| [`DeviceTemplateRefreshWorker.cs`](../../ViewOwl.Grabber.WebAPI/BackgroundServices/DeviceTemplateRefreshWorker.cs) | Schedules grabs; dispatches single-frame or multi-frame (Class C) capture |

---

## Why headless Chromium?

The alternative approaches and why they were rejected:

**Canvas rendering libraries (SkiaSharp, ImageSharp drawing):** Can't render CSS, web fonts, or external data. Every template would need a custom rendering implementation.

**wkhtmltopdf / WeasyPrint:** Outdated rendering engines. Poor CSS 3 support. No canvas 2D. No `fetch()`.

**Puppeteer-controlled Chrome:** This is exactly what ViewOwl uses (via PuppeteerSharp). Full CSS, full JS, full canvas, real network access.

**Chromium on ARM64:** The key constraint. The bundled Chrome-for-Testing binary is x64-only. ViewOwl uses the system `chromium-browser` package installed via `apt` on the ARM64 server. PuppeteerSharp is configured to use it instead of downloading its own binary.

---

## Chrome.cs — internal design

### Tab management

`Chrome.cs` does not open a new browser window per template. It manages a pool of tabs via `ITabManager`. Tabs are opened on first use and optionally kept alive between grabs (`CloseTabAfterCapture = false`) for animated templates that rely on canvas state.

The **KeepTabAlive** path (for animated Class B templates like particle systems): the tab stays open, JS continues running between captures. Each grab calls `SetViewportAsync` + `ReloadAsync` to reset the page to a deterministic state.

For Class C, `CloseTabAfterCapture = true` — tabs are ephemeral. One tab per frame, then closed.

### Why `ReloadAsync` after `SetViewportAsync`?

JavaScript reads `window.innerWidth` / `window.innerHeight` at page load. If we only resize the viewport, the already-running JS has the old dimensions. Reloading forces a full re-layout at the correct size.

This is why responsive canvas templates use `window.innerWidth` in their init code — not hardcoded pixel values.

### CSS injection

Before every screenshot, Chrome injects:

```javascript
* { image-rendering: pixelated !important; }
```

This prevents Chromium's sub-pixel antialiasing from producing intermediate colours that quantise badly to BGR565. When the Monochrome DB flag is set, it also injects:

```javascript
html { filter: grayscale(1); }
```

### CRC dedup

After conversion, `Chrome.cs` computes a CRC32 of the output bytes and compares it to the previous render for the same device:

```csharp
private readonly ConcurrentDictionary<Guid, uint> _frameCrcCache = new();
```

If the CRC matches, the `.bin` write is skipped entirely. This prevents the UDP Server from receiving spurious "new frame" signals when nothing has changed (e.g., a clock template grabbed one second before the minute changes).

### Atomic write

```csharp
await File.WriteAllBytesAsync(tmpPath, bytes, ct);
File.Move(tmpPath, binPath, overwrite: true);
```

The UDP Server may be reading `{guid}.bin` at the same moment as the Grabber is writing a new one. The `.tmp` → rename pattern ensures the server always sees a complete file, never a partial write.

---

## BGR565 conversion

BGR565 is the native pixel format for ILI9341 and ST7796 SPI LCD controllers. Sending it directly from the `.bin` file to the SPI bus requires zero conversion on the ESP32.

Layout (16 bits per pixel, little-endian):

```
Bits 15–11: Blue  (5 bits, values 0–31)
Bits 10–5:  Green (6 bits, values 0–63)
Bits 4–0:   Red   (5 bits, values 0–31)
```

The converter reads `Image<Rgb24>` pixels from ImageSharp and packs them:

```csharp
ushort b = (ushort)((pixel.B >> 3) & 0x1F);
ushort g = (ushort)((pixel.G >> 2) & 0x3F);
ushort r = (ushort)((pixel.R >> 3) & 0x1F);
ushort bgr565 = (ushort)((b << 11) | (g << 5) | r);
```

Written little-endian to match what the LCD controller expects.

### Compression

The default output for Class C frames is **palette+RLE** or **palette+LZ4**, flagged by the first byte of the payload:

| First byte | Format |
|---|---|
| `0x00` | Raw BGR565 + flag byte (legacy) |
| `0x01` | Palette (up to 256 colours) + RLE |
| `0x03` | Palette + LZ4 |

For a 480×320 wireframe template with a dark background and ~100 green lines, palette+RLE compresses the 307,200-byte raw frame down to ~4,000 bytes. This dramatically reduces the UDP transfer time for Class C batches.

---

## SiteTemplateController — the HTTP bridge

**Route:** `GET /SiteTemplate?name=t{id}.html`

Chromium cannot open `file://` URLs on Linux ARM64 (sandboxing policy). Every template — whether from the DB or from the `SitesTemplates/` folder — is served through this controller as a plain HTTP response.

For DB templates, `SiteTemplateController` reads the template's `HtmlContent` from the database and returns it as `text/html`. For filesystem templates, it serves the file directly.

This design has a secondary benefit: templates can reference relative assets (`./img/logo.png`) that are served by the same controller, which is impossible with `file://` loading.

**Preview endpoint:** `GET /SiteTemplate/preview?name=t{id}.png` returns the most recently grabbed `.png` from ExchangeFolder. Used by the landing page gallery to show thumbnails without decoding BGR565.

---

## DeviceTemplateRefreshWorker — the scheduler

**File:** [`BackgroundServices/DeviceTemplateRefreshWorker.cs`](../../ViewOwl.Grabber.WebAPI/BackgroundServices/DeviceTemplateRefreshWorker.cs)

Runs every 5 minutes. For each template assigned to at least one device:

1. Reads `data-vow-class`, `data-vow-refresh`, `data-vow-frames`, `data-vow-fps` from the template HTML.
2. Skips if `data-vow-refresh == 0` and it's not the first grab after assignment.
3. For **Class C**: computes SHA256 of `HtmlContent`, compares to `t{id}_{w}x{h}.htmlhash` sidecar. If hash and batch CRC match → skip. Otherwise: grabs N frames via `GrabMultiframeAtResolutionAsync`, writes `{guid}_batch.json` manifest.
4. For **Class A/B**: calls `GrabAtResolutionAsync` → single screenshot.
5. For each distinct connected device resolution — grabs separately.
6. Calls `PruneStaleGrabsAsync` — keeps the 4 most recent `.bin`/`.png` files per template×resolution, deletes the rest.

---

## Performance characteristics

| Operation | Time (ARM64, single device) |
|---|---|
| Chrome tab open + `networkidle0` | 1.5–3 s |
| PNG screenshot (480×320) | ~200 ms |
| ImageSharp resize + BGR565 convert | ~40 ms |
| Palette+RLE compress | ~5 ms |
| File write (`.tmp` → rename) | ~2 ms |
| **Total per frame (Class A/B)** | **~2–4 s** |
| **Total per frame (Class C, 12 frames)** | **~25–45 s** |

The bottleneck for Class C is Chrome tab lifecycle (open → load → screenshot → close) × N. Keeping tabs alive between frames (`CloseTabAfterCapture = false`) reduces this but requires deterministic JS state — only safe for templates without external API calls.
