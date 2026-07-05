# Grabber API Reference

**Project:** `ViewOwl.Grabber.WebAPI`  
**Framework:** ASP.NET Core 8  
**Runtime target:** `linux-arm64` self-contained single-file binary  
**Port:** 5000/TCP (configurable)  
**Entry point:** [`Program.cs`](../../ViewOwl.Grabber.WebAPI/Program.cs)

The Grabber API is the central hub: it runs headless Chromium, serves templates to it over HTTP, schedules periodic re-grabs, and accepts direct frame pushes from guest devices.

---

## Startup sequence

`Program.cs` configures DI, then at startup:

1. Calls `IGrabber.InitAsync()` — launches headless Chromium, opens the warmup page (`servicepage.html`)
2. Starts all `IHostedService` background workers
3. Begins accepting HTTP requests

Chromium launch can take 3–5 seconds on a cold ARM64 server. The warmup page (`SitesTemplates/servicepage.html`) pre-warms the V8 JIT so the first real template grab is faster.

---

## Controllers

### `SiteTemplateController`

**Route:** `GET /SiteTemplate`  
**File:** [`Controllers/SiteTemplateController.cs`](../../ViewOwl.Grabber.WebAPI/Controllers/SiteTemplateController.cs)

Serves HTML templates to headless Chromium over HTTP. This exists because Chromium on Linux ARM64 sandboxes `file://` access — all local templates must be served over HTTP.

| Endpoint | Purpose |
|---|---|
| `GET /SiteTemplate?name=t{id}.html` | Returns DB template HTML as `text/html` |
| `GET /SiteTemplate?name=*.html` | Returns file from `SitesTemplates/` folder |
| `GET /SiteTemplate/preview?name=t{id}.png` | Returns most recent grabbed PNG preview |

The `t{id}.html` pattern routes to the DB; any other name routes to `SitesTemplates/` on disk. A regex `^t(\d+)\.html$` distinguishes the two.

For the preview endpoint, the controller looks up the most recent `GrabLog` entry for the template, constructs the expected `.png` path in `ExchangeFolder`, and serves it directly. No BGR565 decoding at request time.

---

### `GuestDeviceController`

**Route:** `POST /api/guest/...`  
**File:** [`Controllers/GuestDeviceController.cs`](../../ViewOwl.Grabber.WebAPI/Controllers/GuestDeviceController.cs)

Public API for unauthenticated guest devices flashed from the landing page. No auth token required — the device GUID is the caller's credential.

#### `POST /api/guest/device` — Register

Registers a freshly flashed device. Idempotent: if the GUID already exists, returns `Created=false` and triggers an immediate grab with the new template if it changed.

Request body:
```json
{
  "guid": "57ca9af6-6f38-4005-9521-009e340141e2",
  "templateName": "sf-nostromo-boot.html",
  "templateId": 42,
  "displayWidth": 480,
  "displayHeight": 320
}
```

After registration, calls `TriggerImmediateGrabAsync` so the `.bin` is ready before the device sends its first UDP HELLO.

#### `POST /api/guest/device/{guid}/frame` — Push frame

Accepts a raw BGR565 frame or compressed frame from the landing page push loop. Validates the format (flag byte or exact raw size), then writes atomically to `ExchangeFolder/{guid}.bin`.

Frame format validation:
```csharp
int  expectedRaw = device.DisplayWidth * device.DisplayHeight * 2;
bool isLegacyRaw = frameBytes.Length == expectedRaw;
bool isKnownFlag = frameBytes.Length >= 2 &&
                   (frameBytes[0] == 0x00 ||   // raw + flag
                    frameBytes[0] == 0x01 ||   // palette+RLE
                    frameBytes[0] == 0x03);    // palette+LZ4
```

Max payload: 1 MB (`[RequestSizeLimit(1_048_576)]`).

---

## Background Services

### `DeviceTemplateRefreshWorker`

**File:** [`BackgroundServices/DeviceTemplateRefreshWorker.cs`](../../ViewOwl.Grabber.WebAPI/BackgroundServices/DeviceTemplateRefreshWorker.cs)

The main content-refresh engine. Runs every 5 minutes.

For each template assigned to at least one active device, and for each unique device resolution:

1. **Read metadata** from `HtmlContent` via `TemplateMetadataParser`
2. **Check skip conditions:**
   - `data-vow-refresh == 0` and not first-time grab → skip
   - Class C: HTML SHA256 matches sidecar hash AND batch CRC matches manifest → skip
3. **Grab:**
   - Class A/B: `GrabAtResolutionAsync` → single screenshot
   - Class C: `GrabMultiframeAtResolutionAsync` → N screenshots at `?vow_frame=0..N-1`
4. **Write manifest** for Class C: `{guid}_batch.json` with frame count, fps, batch CRC
5. **Prune** stale files: keeps 4 most recent `.bin`/`.png` per template×resolution

The skip optimisation for Class C (HTML hash sidecar) prevents re-rendering 12 Chrome frames every 5 minutes when the template HTML hasn't changed. A 12-frame Class C grab takes 25–45 seconds on ARM64; running it unnecessarily would keep Chromium hot and delay other grabs.

---

### `GuestDeviceRefreshWorker`

**File:** [`BackgroundServices/GuestDeviceRefreshWorker.cs`](../../ViewOwl.Grabber.WebAPI/BackgroundServices/GuestDeviceRefreshWorker.cs)

Same logic as `DeviceTemplateRefreshWorker` but for guest devices registered via the landing page (no user account, no DB template assignment — template is embedded in the device registration record).

---

### `GrabberWorker`

**File:** [`BackgroundServices/GrabberWorker.cs`](../../ViewOwl.Grabber.WebAPI/BackgroundServices/GrabberWorker.cs)

Legacy service. Reads `GrabbedList.json` and grabs external URLs on a cycle. Predates the DB-template system. Will be removed once all legacy `lw-*.html` templates are migrated to the DB.

---

### `DbCleanupWorker`

**File:** [`BackgroundServices/DbCleanupWorker.cs`](../../ViewOwl.Grabber.WebAPI/BackgroundServices/DbCleanupWorker.cs)

Runs nightly:
- Deletes orphaned `.bin` / `.png` files in `ExchangeFolder` (no matching DB record)
- Runs `PRAGMA VACUUM` on the SQLite database to reclaim space from deleted template records
- Trims `GrabLog` entries older than the retention window

---

### `DeviceMonitorService`

**File:** [`BackgroundServices/DeviceMonitorService.cs`](../../ViewOwl.Grabber.WebAPI/BackgroundServices/DeviceMonitorService.cs)

Tracks connected device status. Publishes SignalR events to the admin dashboard when device state changes (connected, disconnected, template changed).

---

## Configuration

All configuration via `IOptions<T>` — `IConfiguration` is never injected directly into services.

**`appsettings.json`:**
```json
{
  "Shared": {
    "ExchangeFolder": "/opt/viewowl/exchange/",
    "WebApiBaseUrl": "http://localhost:5000"
  },
  "Grabber": {
    "GrabbedListFileName": "GrabbedList.json"
  }
}
```

**Config types:**

| Class | File | Fields |
|---|---|---|
| `SharedConfig` | [`ViewOwl.Config/SharedConfig.cs`](../../ViewOwl.Config/SharedConfig.cs) | `ExchangeFolder`, `WebApiBaseUrl` |
| `GrabberConfig` | [`ViewOwl.Config/GrabberConfig.cs`](../../ViewOwl.Config/GrabberConfig.cs) | `GrabbedListFileName` |

---

## Dependencies

| Package | Purpose |
|---|---|
| `PuppeteerSharp` | Headless Chromium control (Chrome DevTools Protocol) |
| `SixLabors.ImageSharp` | PNG decode, resize, pixel access |
| `Microsoft.EntityFrameworkCore.Sqlite` | Template/device DB |
| `Microsoft.AspNetCore.SignalR` | Real-time dashboard events |

---

## Concurrency model

- **One Chrome instance**, shared across all workers via `IGrabber` (singleton)
- **Tab-per-grab**: each grab opens a tab, takes a screenshot, closes it (or keeps it alive for animated templates)
- **No parallel grabs**: `DeviceTemplateRefreshWorker` processes templates sequentially — Chrome is single-threaded by design and concurrent tab operations cause flaky screenshots
- **Parallel per-resolution**: for a template with 480×320 and 320×240 devices, both resolutions are grabbed in sequence, not in parallel
