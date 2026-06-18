# Frontend Reference

**Framework:** React 18  
**Build tool:** Vite  
**UI library:** React Flow (node-based canvas)  
**Real-time:** SignalR WebSocket  
**Directory:** [`ViewOwl.Grabber.WebAPI/ClientApp/src/`](../../ViewOwl.Grabber.WebAPI/ClientApp/src/)  
**Dev port:** 5173 (Vite), proxies `/api` and `/hubs` to .NET at 5085  
**Production:** Served as static files from `wwwroot/` by the .NET host

---

## Application shell

### Authentication gate

On load, the app checks `localStorage` for a JWT token. If absent or expired:
- Redirects to `/login.html` (served by .NET from `wwwroot/`)
- `login.html` uses plain `js/auth.js` — no React build step needed
- After login, `auth.js` stores the token in `localStorage` at the current origin and redirects to `/dashboard/`

**Important:** `localStorage` is origin-scoped (`host:port`). In Vite dev mode (`:5173`), the token stored by `login.html` at `:5085` is invisible. The user must log in once through Vite at `:5173` to seed the token at the correct origin.

### Routing

The dashboard is a single-page application. React Router handles:

| Path | Component | Notes |
|---|---|---|
| `/dashboard/` | `FlowCanvas` | Main canvas — default view |
| `/dashboard/pipeline` | `PipelineView` | ISA-101-style event timeline |
| `/dashboard/security` | Security panel | Admin only |

---

## `FlowCanvas.jsx` — Main dashboard

The central component of the admin panel. Uses **React Flow** to render a visual node graph where:
- **TemplateNode** = a registered template (HTML thumbnail, grab status, class badge)
- **DeviceNode** = a registered display device (live status, RSSI, uptime, firmware version)
- **Edges** = active connections (which template is displayed on which device)

### State

`FlowCanvas` holds the global dashboard state (`dashboardState`) loaded from `GET /api/dashboard/state` on mount. This snapshot includes:
- All user's templates (with metadata parsed from `HtmlContent`)
- All user's devices (with live hardware stats)
- All active connections
- Current user info

**Live updates** arrive via SignalR — `FlowCanvas` subscribes to hub events and patches `dashboardState` without a full reload.

### Node positions

Node `x, y` positions are persisted to `POST /api/flow-layout` (debounced, fires 500ms after drag-stop). On next load, `GET /api/flow-layout` restores them. Nodes not in the saved layout get auto-positioned.

### `templateDeviceDims`

A computed map of `templateId → { w, h }` built from active connections:

```javascript
templateDeviceDims[conn.templateId] = { w: dev.displayWidth, h: dev.displayHeight };
// Uses the smallest connected device (downscale is safe; upscale is blurry)
data.pushWidth  = dims?.w ?? 480;
data.pushHeight = dims?.h ?? 320;
```

Used by the frame push loop to scale the canvas to the actual device resolution before converting to BGR565. Updated on both initial load and on `ConnectionsChanged` SignalR events.

---

## Node components

### `TemplateNode`

**File:** [`components/nodes/TemplateNode.jsx`](../../ViewOwl.Grabber.WebAPI/ClientApp/src/components/nodes/)

Displays:
- Template name and class badge (`A` / `B` / `C`)
- Category tag
- Live preview thumbnail (from `GET /SiteTemplate/preview?name=t{id}.png`)
- Grab status (idle / grabbing / last grab time)
- **Action bar** on hover: Grab, Edit, Push, Delete

**Grab button:** `POST /api/templates/{id}/grab` → triggers immediate Chrome screenshot.

**Push button:** Activates the browser-side push loop (see Push loop section below).

**Edit button:** Opens `TemplateEditorModal` with the full HTML source.

### `DeviceNode`

**File:** [`components/nodes/DeviceNode.jsx`](../../ViewOwl.Grabber.WebAPI/ClientApp/src/components/nodes/)

Displays:
- Device name and status dot (green/grey)
- Display resolution badge (480×320 / 320×240)
- Firmware version
- Live stats: WiFi RSSI (dBm), free heap (KB), uptime
- MAC address (hardware fingerprint, v1.2.18+)
- Last frame CRC

**Action bar:** Rename, Restart (sends reboot command), Delete, Burn (open flash wizard).

### `KnockingPanel`

**File:** [`components/KnockingPanel.jsx`](../../ViewOwl.Grabber.WebAPI/ClientApp/src/components/KnockingPanel.jsx)

When a device sends a HELLO with an unregistered token, it appears in the "Knocking" panel — a list of devices waiting to be admitted. The admin clicks "Register" to create a `Device` record and assign it to a user.

This flow is used for newly flashed devices before they are provisioned.

---

## Template management

### `TemplateEditorModal`

Full-screen HTML editor. Features:
- Monaco-style text area with syntax highlighting
- Live preview iframe (the HTML rendered in the browser at 480×320)
- `data-vow-*` metadata fields parsed from the HTML and shown as editable inputs
- Monochrome toggle (affects server grab, not browser preview)
- IsPublic toggle (makes template appear in landing page gallery)
- Save → `PUT /api/templates/{id}` → SignalR `TemplateUpdated` event → all connected dashboards update

### Gallery / starter templates

`GET /api/templates/starters` returns a set of built-in template HTML bodies (one per category) that users can fork as starting points. These are embedded in the server binary, not in the DB.

### Import / Export

| Endpoint | Format |
|---|---|
| `GET /api/templates/export` | ZIP file containing all user's templates as `.html` files |
| `POST /api/templates/import` | ZIP or single `.html` file; creates or updates templates by name |

---

## Browser-side frame push loop

When the **Push** button is active on a `TemplateNode`, the dashboard runs a push loop in JavaScript:

```javascript
async function _doPushFrame() {
  // 1. Find the preview iframe for this template
  const iframe = document.querySelector(`iframe[data-template-id="${id}"]`);
  const canvas = iframe?.contentDocument?.querySelector('canvas');

  if (!canvas) {
    _stopPushLoop('no canvas');   // DOM-only templates unsupported
    return;
  }

  // 2. Read canvas pixels at 480×320
  const offscreen = new OffscreenCanvas(pushWidth, pushHeight);
  const ctx = offscreen.getContext('2d');
  ctx.drawImage(canvas, 0, 0, pushWidth, pushHeight);  // scales if needed
  const imageData = ctx.getImageData(0, 0, pushWidth, pushHeight);

  // 3. Convert RGBA → BGR565 + palette+RLE compression
  const compressed = paletteRleCompress(rgbaToBgr565(imageData));

  // 4. POST to server
  const res = await fetch(`/api/templates/${id}/frame`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/octet-stream', 'Authorization': `Bearer ${token}` },
    body: compressed
  });

  if (!res.ok) { _stopPushLoop(`server ${res.status}`); return; }

  // 5. Schedule next frame (1–5 second interval)
  pushTimer = setTimeout(_doPushFrame, pushIntervalMs);
}
```

**Stop conditions:**
- No `<canvas>` in the iframe (DOM-only template)
- Server returns non-200
- `SecurityError` (canvas tainted by cross-origin `<img>` — fonts don't taint)
- User clicks Push button again (toggle off)

**Note on canvas taint:** Google Fonts and other web fonts loaded via CSS `@font-face` do **not** taint the canvas. Only cross-origin `<img>` elements do. Templates using VT323 or other Google Fonts can be pushed safely.

---

## Real-time — SignalR

**Hub:** `/hubs/viewowl` (WebSocket, requires JWT auth)  
**Client:** `@microsoft/signalr` npm package

### Connection

```javascript
const connection = new signalR.HubConnectionBuilder()
  .withUrl('/hubs/viewowl', { accessTokenFactory: () => getToken() })
  .withAutomaticReconnect()
  .build();
```

`withAutomaticReconnect()` handles transient disconnects — the dashboard reconnects automatically after server restarts or network hiccups.

### Events the dashboard listens to

| Event | Payload | Action |
|---|---|---|
| `TemplateCreated` | `TemplateDto` | Add node to canvas |
| `TemplateUpdated` | `TemplateDto` | Update node content/metadata |
| `TemplateDeleted` | `id` | Remove node and its edges |
| `ConnectionsChanged` | `{ connections[] }` | Rebuild edges, update `templateDeviceDims` |
| `GrabStarted` | `GrabLogDto` | Show "grabbing" spinner on TemplateNode |
| `GrabCompleted` | `GrabLogDto` | Clear spinner, refresh preview thumbnail |
| `DeviceStatusChanged` | `DeviceStatusDto` | Update status dot, hardware stats on DeviceNode |
| `DeviceKnocking` | `{ token, displayWidth, displayHeight }` | Add entry to KnockingPanel |

### Groups

- **User group:** Each authenticated connection joins `user-{userId}` — receives that user's events only
- **Admin group:** Admin users additionally join `admin` — receives security events and cross-user notifications

---

## `PipelineView` — Event timeline

**File:** [`components/PipelineView.jsx`](../../ViewOwl.Grabber.WebAPI/ClientApp/src/components/PipelineView.jsx)

Visualises the end-to-end content delivery pipeline as an ISA-101-style multilane timeline:

```
Template  ──[grab_queued]──[grab_completed]──────────────────────────────────►
                                             │
UDP       ────────────────────────────[session_opened]──[frame_sent]──[done]──►
                                                                          │
Device    ─────────────────────────────────────────────────────────[online]───►
```

Data from `GET /api/pipeline/events` — the 50 most recent `PipelineEvent` records for the current user's devices and templates. Refreshes every 10 seconds or on SignalR push.

Useful for debugging: if `grab_completed` appears but `session_opened` never follows, the ExchangeFolder path is wrong or the UDP Server can't see the file. If `session_opened` appears but `device_online` doesn't follow, the device is offline or rejected the frame.

---

## BURN wizard — `BurnModal`

**File:** [`components/Burn/BurnModal.jsx`](../../ViewOwl.Grabber.WebAPI/ClientApp/src/components/Burn/)

The web-based ESP32 flash wizard. Uses `esp-web-tools` (`<esp-web-install-button>`) to flash firmware directly from the browser via Web Serial API. The library is **self-hosted** under `wwwroot/vendor/esp-web-tools/` (Apache-2.0, no third-party CDN — keeps the public landing free of any external script), and the Material flash dialog is themed to the ViewOwl palette via MD3 custom properties in `index.html`.

Steps:
1. User selects display size (480×320 or 320×240) from a dropdown
2. User selects a template from the gallery
3. The wizard sends a pre-registration request to `POST /api/guest/device` with the chosen template and display dims — the server creates a `GuestDevice` record and triggers an immediate grab
4. `<esp-web-install-button>` flashes the correct firmware `.bin` from `wwwroot/firmware/v{version}/`
5. After flash, the device boots, opens a serial terminal for WiFi provisioning
6. On first HELLO, the server delivers the pre-grabbed frame immediately (no wait for the 5-minute cycle)

**Current limitation:** Step 3 pre-registration uses `fetch()` without a JWT header — results in 401 for authenticated endpoints. This is a known pending bug; the workaround is to register the device from the admin panel manually.

---

## `HUD` components

Overlay components shown on the canvas:

| Component | Purpose |
|---|---|
| `TransferSpeedHUD` | Real-time UDP transfer speed (KB/s) per device, updated via SignalR |
| `GrabQueueHUD` | Count of pending grabs in the `GrabChannel` queue |
| `ServerStatusHUD` | Grabber API health, Chromium status, ExchangeFolder disk usage |

---

## Hooks

| Hook | File | Purpose |
|---|---|---|
| `useApi(url)` | `hooks/useApi.js` | Fetch with JWT auth header, error handling, loading state |
| `useSignalR(event, handler)` | `hooks/useSignalR.js` | Subscribe to a hub event; auto-cleanup on unmount |
| `useDashboardState()` | `hooks/useDashboardState.js` | Load and patch the global `DashboardState` snapshot |
| `usePushLoop(templateId)` | `hooks/usePushLoop.js` | Manage the browser frame push loop lifecycle |

---

## State management

No Redux or Zustand. State is managed at the `FlowCanvas` level via `useState` and `useReducer`:

- `dashboardState` — full snapshot, patched by SignalR events
- `selectedNodeId` — which node's detail panel is open
- `pushActiveFor` — set of template IDs currently in push mode
- `flowNodes` / `flowEdges` — React Flow node/edge arrays, derived from `dashboardState`

The derivation from `dashboardState` to `flowNodes`/`flowEdges` happens in a `useMemo` on every `dashboardState` change. No separate "flow state" — the React Flow representation is always computed from the business state.
