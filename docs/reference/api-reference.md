# API Reference

**Base URL:** `https://your-server-domain`  
**Auth:** Bearer JWT in `Authorization` header (except public and guest endpoints)  
**Format:** JSON (request and response) unless noted  
**Content-Type:** `application/json` unless noted

---

## Authentication

### `POST /api/auth/login`

Authenticate and receive a JWT token.

**No auth required.**

**Request:**
```json
{ "login": "admin", "password": "secret" }
```

**Response 200:**
```json
{ "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9..." }
```

**Response 401:** Invalid credentials (same message regardless of which field is wrong).

---

## Current user

### `GET /api/me`

```json
{ "id": 1, "login": "admin", "role": "Admin", "sandboxEnabled": false }
```

### `GET /api/me/sessions`

```json
[
  {
    "sessionId": "3f2504e0-...",
    "ipAddress": "192.168.1.10",
    "userAgent": "Mozilla/5.0...",
    "createdAt": "2026-05-17T10:00:00Z",
    "lastSeenAt": "2026-05-17T14:23:00Z",
    "isCurrent": true
  }
]
```

### `DELETE /api/me/sessions/{sessionId}`

Revokes the specified session. Returns 204 No Content.

### `DELETE /api/me/sessions`

Revokes all sessions except the current one. Returns 204 No Content.

---

## Templates

### `GET /api/templates`

Returns all templates owned by the authenticated user.

```json
[
  {
    "id": 42,
    "name": "Nostromo Boot",
    "vowClass": "C",
    "vowCategory": "scifi",
    "vowFrames": 12,
    "vowFps": 3,
    "vowRefresh": 0,
    "isPublic": false,
    "monochrome": false,
    "createdAt": "2026-04-01T12:00:00Z"
  }
]
```

`vowClass`, `vowCategory`, `vowFrames`, `vowFps`, `vowRefresh` are parsed from `HtmlContent` — they are not stored as separate DB columns.

### `POST /api/templates`

Create a new template.

**Request:**
```json
{ "name": "My Template", "htmlContent": "<!DOCTYPE html>..." }
```

**Response 201:** Created template DTO (same shape as GET list item, plus `htmlContent`).

### `GET /api/templates/{id}`

Returns template including `htmlContent`. 404 if not found or not owned by current user.

### `PUT /api/templates/{id}`

Update template content or name.

**Request:**
```json
{ "name": "New Name", "htmlContent": "<!DOCTYPE html>..." }
```

**Response 200:** Updated template DTO.

### `DELETE /api/templates/{id}`

Deletes template, all its connections, grab logs, and associated `.bin`/`.png` files in ExchangeFolder.

**Response 204 No Content.**

### `PATCH /api/templates/{id}/public`

**Request:** `{ "isPublic": true }`  
**Response 200.** Makes template visible in the public gallery at `GET /api/public-templates`.

### `POST /api/templates/{id}/grab`

Enqueue an immediate grab. The server opens a Chrome tab, takes a screenshot, and writes `{TokenGuid}.bin` to ExchangeFolder. For Class C templates, captures all `data-vow-frames` frames.

**Response 202 Accepted:** `{ "grabLogId": 99 }`

### `GET /api/templates/{id}/log`

Recent grab log entries for this template.

```json
[
  {
    "id": 99,
    "tokenGuid": "b2c3d4e5-...",
    "width": 480, "height": 320,
    "requestedAt": "2026-05-17T14:20:00Z",
    "completedAt": "2026-05-17T14:20:03Z",
    "success": true
  }
]
```

### `GET /api/templates/{id}/preview-image`

Returns the most recent grabbed frame as `image/png`. Used by the admin panel thumbnail and the landing page gallery.

**Response:** PNG binary or 404 if no successful grab exists.

### `GET /api/templates/{id}/stats`

```json
{
  "totalGrabs": 142,
  "successfulGrabs": 140,
  "failedGrabs": 2,
  "lastGrabAt": "2026-05-17T14:20:03Z",
  "connectedDevices": 2
}
```

### `GET /api/templates/starters`

Returns a list of built-in starter template HTML bodies (one per category). Users can fork these as a starting point.

**No auth required.**

### `GET /api/templates/export`

Downloads a ZIP file containing all the user's templates as `.html` files.

**Response:** `application/zip`, filename `viewowl-templates-{date}.zip`

### `POST /api/templates/import`

**Content-Type:** `multipart/form-data`  
**File:** `.zip` (multiple templates) or `.html` (single template)

Creates new templates or updates existing ones (matched by name). Returns a summary of imported/updated counts.

---

## Devices

### `GET /api/devices`

All devices owned by the current user.

```json
[
  {
    "id": 17,
    "name": "Living Room",
    "token": "b2c3d4e5f6a74b8c9d0e1f2a3b4c5d6e",
    "displayType": "ST7796",
    "displayWidth": 480,
    "displayHeight": 320,
    "firmwareVersion": "OWLVIEW-1.2.35-b42",
    "macAddress": "AA:BB:CC:DD:EE:FF",
    "status": "Online",
    "wifiRssi": -52,
    "freeHeap": 180000,
    "uptimeSeconds": 86400,
    "lastSeenAt": "2026-05-17T14:23:00Z",
    "activeTemplateId": 42
  }
]
```

### `POST /api/devices`

Register a new device manually.

**Request:** `{ "name": "Kitchen", "token": "...", "displayWidth": 320, "displayHeight": 240 }`  
**Response 201:** Created device DTO.

### `GET /api/devices/{id}`

Single device with full stats. 404 if not found or not owned.

### `DELETE /api/devices/{id}`

Deletes device, all connections, and clears its frame from ExchangeFolder.  
**Response 204.**

### `PATCH /api/devices/{id}/name`

**Request:** `{ "name": "New Label" }`  
**Response 200.**

### `PATCH /api/devices/{id}/active-template`

**Request:** `{ "templateId": 42 }` or `{ "templateId": null }` to unassign.  
**Response 200.** Triggers an immediate grab at the device's resolution.

### `POST /api/devices/{id}/template/{templateId}`

Create a connection between device and template. Equivalent to drawing an edge in the React Flow canvas.

**Response 201:** Connection DTO.

### `POST /api/devices/{id}/restart`

Enqueue a reboot command. The device receives it on its next HELLO cycle via the UDP Server's `PushLoop`.

**Response 202.**

### `PATCH /api/devices/{id}/token`

Rotate the device token. Used if the old token was compromised.

**Request:** `{ "newToken": "..." }`  
**Response 200.** The firmware must be reflashed with the new token value in `config.h`.

### `GET /api/devices/{id}/stats`

Device uptime ratio and RSSI history for the last 24 hours (from `DevicePings` table).

### `POST /api/devices/{id}/create-status-template`

Auto-generates a `device_status.html` template pre-configured for this device (token baked in). Assigns it immediately.

---

## Connections

### `GET /api/connections`

All connections (template↔device links) for the current user.

### `POST /api/connections`

**Request:** `{ "templateId": 42, "deviceId": 17 }`

**Business rules enforced:**
- Device can have only one active connection; creating a second replaces the first
- Both template and device must be owned by the current user

**Response 201:** Connection DTO.

### `DELETE /api/connections/{id}`

Remove connection. Device reverts to no content. **Response 204.**

### `GET /api/connections/device/{deviceId}`

Active connection for a specific device.

---

## Frame push (browser → device)

### `POST /api/templates/{id}/frame`

**Content-Type:** `application/octet-stream`  
**Auth:** Bearer token required.  
**Rate limit:** 60 fps maximum (≥ 16ms between requests from same user).

Push a raw BGR565 or compressed frame directly to the connected device. The server validates the format and writes the frame to `ExchangeFolder/{TokenGuid}.bin`. The UDP Server's `PushLoop` delivers it to the device within 500ms.

**Frame format:** first byte is a flag:
- `0x01` — palette+RLE
- `0x03` — palette+LZ4
- No flag prefix — raw BGR565 (payload length must equal `width × height × 2`)

**Response 204 No Content.**  
**Response 400** — invalid frame format or size.  
**Response 429** — rate limit exceeded.

---

## Pipeline & observability

### `GET /api/pipeline/events`

Recent `PipelineEvent` records for the current user's devices and templates. Default: last 50.

**Query params:** `?deviceId=17`, `?templateId=42`, `?type=grab_completed`, `?limit=100`

### `GET /api/pipeline/summary`

Aggregated pipeline health for each template and device:

```json
{
  "templates": [
    { "templateId": 42, "lastGrab": "completed", "lastGrabAt": "...", "successRate": 0.98 }
  ],
  "devices": [
    { "deviceId": 17, "lastSeen": "...", "uptimeRatio": 0.99 }
  ]
}
```

---

## Dashboard

### `GET /api/dashboard/state`

Full snapshot of the current user's state. Loaded once on dashboard startup; kept fresh via SignalR.

```json
{
  "user": { "id": 1, "login": "admin", "role": "Admin" },
  "templates": [...],
  "devices": [...],
  "connections": [...],
  "grabQueueDepth": 0
}
```

---

## Public (no auth)

### `GET /api/public-templates`

Templates with `IsPublic = true`. Used by the landing page gallery.

```json
[
  {
    "id": 42,
    "name": "Nostromo Boot",
    "vowClass": "C",
    "vowCategory": "scifi",
    "previewUrl": "/SiteTemplate/preview?name=t42.png"
  }
]
```

### `GET /SiteTemplate?name=t{id}.html`

Returns template HTML. Called by headless Chromium during grabs.  
**No auth required** — Chromium runs without a session cookie.

### `GET /SiteTemplate/preview?name=t{id}.png`

Returns the most recent grabbed frame as PNG. Used by gallery thumbnails.

---

## Guest devices (landing page)

### `POST /api/guest/device`

Register a newly flashed device. No auth required.

**Request:**
```json
{
  "guid": "b2c3d4e5-f6a7-4b8c-9d0e-1f2a3b4c5d6e",
  "templateName": "sf-nostromo-boot.html",
  "templateId": 42,
  "displayWidth": 480,
  "displayHeight": 320
}
```

**Response 201:** `{ "guid": "...", "templateName": "...", "created": true }`  
**Response 200:** `{ "created": false }` — GUID already registered (idempotent re-registration).

### `POST /api/guest/device/{guid}/frame`

Same as `POST /api/templates/{id}/frame` but for guest devices. No auth required.

**Content-Type:** `application/octet-stream`  
**Max size:** 1 MB.  
**Response 204.**

---

## Admin

### `GET /api/admin/users`
### `POST /api/admin/users`
### `DELETE /api/admin/users/{id}`
### `PATCH /api/admin/users/{id}/sandbox`

See [Auth & Security reference](auth-security.md) for full details.

### `GET /api/admin/security/events`
### `GET /api/admin/security/blocked`
### `POST /api/admin/security/block`
### `DELETE /api/admin/security/block/{ip}`
### `GET /api/admin/security/sessions`

Admin-only. See [Auth & Security reference](auth-security.md).

---

## Flow layout

### `GET /api/flow-layout`

Current user's React Flow node positions.

```json
{
  "nodes": [
    { "id": "template-42", "position": { "x": 120, "y": 80 } }
  ]
}
```

### `POST /api/flow-layout`

Save node positions after a drag. Debounced in the client (fires 500ms after drag-stop).

---

## Internal (localhost only)

These endpoints are called by the UDP Server over loopback. They are blocked for all other callers by `LocalhostOnlyMiddleware`.

### `GET /api/internal/device/{token}/frame`

Resolves a device token to the frame file info the UDP Server should stream.

**Response:**
```json
{
  "deviceId": 17,
  "templateId": 42,
  "tokenGuid": "b2c3d4e5-...",
  "filePath": "/opt/viewowl/exchange/b2c3d4e5-....bin",
  "batchManifestPath": "/opt/viewowl/exchange/b2c3d4e5-...._batch.json"
}
```

`batchManifestPath` is non-null for Class C templates.

### `POST /api/internal/device-heartbeat`

Called on every HELLO and PING packet. Updates online status, last-seen timestamp, IP address, and hardware metadata (firmware version, MAC, display type/dimensions). Records a `DevicePing` row. Pushes `DeviceStatusChanged` and `DevicePingEvent` via SignalR. Also triggers a resolution re-grab if the reported display size differs from the last grab.

### `POST /api/internal/device-transfer-started`

Called when the UDP Server begins streaming a frame (AUTH sent). Pushes `DeviceTransferStarted` SignalR event so the dashboard shows SEND status.

### `POST /api/internal/device-transfer-progress`

Called periodically during an active transfer (every 8 chunks ACKed). Pushes `DeviceTransferProgress` SignalR event for live dashboard progress display.

### `POST /api/internal/device-grab-complete`

Called when a UDP session ends (success or failure). Increments `Device.FramesSentCount` on success. Pushes `DeviceGrabCompleted` SignalR event.

### `POST /api/internal/device/{token}/ping`

Records a `DevicePing` row (session outcome). Returns 204.

### `POST /api/internal/device-knocking`

Called when a device sends a HELLO that cannot be authenticated. Pushes `DeviceKnocking` SignalR event to the dashboard.

### `POST /api/internal/device/{token}/consume-restart`

Called by the UDP Server after each PING. Returns `{ hasPendingRestart, hasPendingFrameRefresh }`. If a `reboot` command is pending, marks it as `sent`. Atomically reads and clears the in-memory `IFrameRefreshQueue` flag for this device.

### `POST /api/internal/deploy-notify`

Called by GitHub Actions to broadcast a deploy lifecycle event to all dashboard clients. Auth via `X-Deploy-Secret` header (pre-shared secret) — no JWT required. Body: `{ "phase": "starting" | "complete", "commit": "abc1234" }`.

---

## Health

### `GET /health`

Kubernetes/Docker liveness probe.  
**Response 200:** `{ "status": "healthy" }` or `{ "status": "degraded", "details": "..." }`

### `GET /api/system/health`

Extended health including Chromium status and ExchangeFolder disk usage.
