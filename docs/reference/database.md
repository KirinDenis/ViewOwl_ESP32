# Database Reference

**Engine:** SQLite  
**ORM:** Entity Framework Core 8  
**DbContext:** `ViewOwlDbContext` ([`ViewOwl.Data/ViewOwlDbContext.cs`](../../ViewOwl.Data/ViewOwlDbContext.cs))  
**Security DbContext:** `SecurityDbContext` ([`ViewOwl.Security/SecurityDbContext.cs`](../../ViewOwl.Security/SecurityDbContext.cs))  
**Migrations:** [`ViewOwl.Data/Migrations/`](../../ViewOwl.Data/Migrations/)

Two separate SQLite databases:
- **Main DB** — all business data (users, devices, templates, connections, logs)
- **Security DB** — IP blocks, security events, session fingerprints (separate for isolation and independent backup)

---

## Main database

### `Users`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `Id` | int | PK, auto-increment | |
| `Login` | string | UNIQUE, NOT NULL | Case-sensitive login name |
| `PasswordHash` | string | NOT NULL | BCrypt hash |
| `Role` | string | NOT NULL | `"Admin"` or `"User"` |
| `SandboxEnabled` | bool | NOT NULL, default `true` | When true, template preview iframe gets `sandbox` attribute — restricts JS in preview only |
| `CreatedAt` | DateTime | NOT NULL | UTC |

**Indexes:** `Login` (unique — enforced at DB level, also at app level via `NameExistsForUserAsync`)

**Notes:**
- Passwords are BCrypt-hashed with work factor 12; the raw password never touches the DB
- `Role = "Admin"` grants access to `AdminUsersController`, `SecurityDashboardController`, and all other users' resources
- `SandboxEnabled` is per-user, toggled by admin via `PATCH /api/admin/users/{id}/sandbox`; does not affect device rendering — only the admin panel's preview iframe

---

### `Devices`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `Id` | int | PK, auto-increment | |
| `Token` | string | UNIQUE, NOT NULL | 32-char hex GUID — matches `TOKEN` in firmware `config.h` |
| `Name` | string | NOT NULL | User-facing label; unique per user (not globally) |
| `UserId` | int | FK → `Users.Id`, NOT NULL | Owner |
| `ActiveTemplateId` | int? | FK → `Templates.Id`, NULL | Currently assigned template; null = no content |
| `CreatedAt` | DateTime | NOT NULL | UTC timestamp of registration |
| `DisplayType` | string? | NULL | `"ILI9341"`, `"ST7796"`, `"ST7789"`, etc. |
| `DisplayWidth` | int? | NULL | Pixels; null until first HELLO with hardware info |
| `DisplayHeight` | int? | NULL | Pixels; null until first HELLO with hardware info |
| `FirmwareVersion` | string? | NULL | e.g. `"OWLVIEW-1.2.35-b42"` |
| `MacAddress` | string? | NULL | Hardware fingerprint (populated from first HELLO, v1.2.18+) |
| `IpAddress` | string? | NULL | Last known IP |
| `WifiRssi` | int? | NULL | dBm, updated on each PING |
| `FreeHeap` | int? | NULL | Bytes, updated on each PING |
| `UptimeSeconds` | int? | NULL | Seconds since last boot, updated on each PING |
| `LastSeenAt` | DateTime? | NULL | UTC timestamp of last PING |
| `Status` | string | NOT NULL, default `"Offline"` | `"Online"` or `"Offline"` |
| `LastFrameCrc` | uint? | NULL | CRC32 of last frame received by device |
| `FramesSentCount` | int | NOT NULL, default `0` | Lifetime counter |
| `LastBootAt` | DateTime? | NULL | Estimated boot time: `pingReceivedAt − uptimeSeconds` |

**Indexes:** `Token` (unique — hot-path for every UDP HELLO), `UserId` (FK)

**Notes:**
- `Token` is a deterministic GUID baked into firmware `config.h` during build — it never changes unless manually rotated via `PATCH /api/devices/{id}/token`
- Hardware metadata columns (`WifiRssi`, `FreeHeap`, `UptimeSeconds`, `MacAddress`) are populated from `POST /api/internal/device-heartbeat` on each HELLO/PING received by the UDP Server
- `Status` is set to `"Online"` on PING received, `"Offline"` by `DeviceMonitorService` after ping timeout

---

### `Templates`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `Id` | int | PK, auto-increment | |
| `Name` | string | NOT NULL | User-facing label; unique per user |
| `UserId` | int | FK → `Users.Id`, NOT NULL | Owner |
| `HtmlContent` | string | NOT NULL | Full HTML source — the source of truth |
| `IsPublic` | bool | NOT NULL, default `false` | Visible in landing page gallery |
| `Monochrome` | bool | NOT NULL, default `false` | Inject `filter:grayscale(1)` before grab |
| `CreatedAt` | DateTime | NOT NULL | UTC |

**Indexes:** `UserId` (FK)

**Notes:**
- `data-vow-*` metadata (class, frames, fps, refresh, category) is **not stored here** — it is parsed from `HtmlContent` on every read by `TemplateMetadataParser.cs`
- `IsPublic = true` makes the template appear in `GET /api/public-templates` (landing page gallery, no auth required)
- Deleting a template cascades: its `Connections`, `GrabLogs`, and associated `.bin`/`.png` files in ExchangeFolder are cleaned up by `DbCleanupWorker`

---

### `Connections`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `Id` | int | PK, auto-increment | |
| `UserId` | int | FK → `Users.Id`, NOT NULL | Owner (denormalised for query convenience) |
| `TemplateId` | int | FK → `Templates.Id`, NOT NULL | The template being displayed |
| `DeviceId` | int | FK → `Devices.Id`, NOT NULL | The display device |
| `CreatedAt` | DateTime | NOT NULL | UTC |

**Indexes:** `UserId`, `DeviceId` (FK), `TemplateId` (FK)

**Business rules enforced at application level:**
- One device can have at most one active connection (enforced in `ConnectionsController` before `CreateAsync`)
- Creating a connection also sets `Device.ActiveTemplateId` via `AssignTemplateAsync`
- Deleting a connection clears `Device.ActiveTemplateId` if it matches

**Notes:**
- `Connection` is a many-to-many join table between `Templates` and `Devices`, scoped per user
- The React Flow dashboard represents connections as edges between TemplateNode and DeviceNode
- When a connection is created, `DeviceTemplateRefreshWorker` schedules an immediate grab at the device's resolution

---

### `GrabLogs`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `Id` | int | PK, auto-increment | |
| `TemplateId` | int | FK → `Templates.Id`, NOT NULL | Which template was grabbed |
| `TokenGuid` | Guid | NOT NULL | The `.bin` file token: `ExchangeFolder/{TokenGuid}.bin` |
| `Width` | int | NOT NULL | Grab resolution |
| `Height` | int | NOT NULL | Grab resolution |
| `RequestedAt` | DateTime | NOT NULL | UTC — when grab was enqueued |
| `CompletedAt` | DateTime? | NULL | UTC — when `.bin` was written; null = pending/failed |
| `Success` | bool? | NULL | `true` = success, `false` = failure, `null` = pending |
| `Message` | string | NOT NULL, default `""` | Human-readable status; error description on failure |

**Indexes:** `(TemplateId, RequestedAt)` — used by DeviceTemplateRefreshWorker to find the most recent successful grab; `(TemplateId, Width, Height)` for resolution-specific lookups

**Notes:**
- `TokenGuid` is what the UDP Server uses to find the `.bin` file: `InternalController.GetFrame(token)` → `GrabLog.TokenGuid` → `{ExchangeFolder}/{TokenGuid}.bin`
- `DeviceTemplateRefreshWorker.PruneStaleGrabsAsync` keeps 4 most recent completed GrabLogs per `(TemplateId, Width, Height)` and deletes the rest along with their `.bin`/`.png` files
- For Class C, one GrabLog is created per batch (not per frame)

---

### `DevicePings`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `Id` | int | PK, auto-increment | |
| `DeviceId` | int | FK → `Devices.Id`, NOT NULL | |
| `Timestamp` | DateTime | NOT NULL | UTC |
| `Success` | bool | NOT NULL | `true` when ping was acknowledged by server |

**Indexes:** `(DeviceId, Timestamp)` — for recent-ping queries and uptime ratio calculations

**Notes:**
- Written on every HELLO/PING received; the `Success` flag reflects whether the server processed the packet without error
- Live telemetry (RSSI, heap, uptime) is stored on the `Device` row itself, not here — `DevicePings` is a lightweight presence log used only for uptime ratio and the device stats chart
- Old ping rows are pruned by `DbCleanupWorker` to prevent unbounded growth

---

### `DeviceCommands`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `Id` | int | PK, auto-increment | |
| `DeviceId` | int | FK → `Devices.Id`, NOT NULL | |
| `CommandType` | string | NOT NULL | `"reboot"`, `"config"`, `"ban"` |
| `Payload` | string? | NULL | JSON payload for config commands |
| `Status` | string | NOT NULL | `"pending"` → `"sent"` → `"ack"` or `"failed"` |
| `CreatedAt` | DateTime | NOT NULL | UTC |
| `ExecutedAt` | DateTime? | NULL | UTC — when status changed from pending |

**Indexes:** `(DeviceId, Status)` — hot-path for pending command polling

**Notes:**
- Commands are queued here and consumed by the UDP Server's `PushLoop` on the next HELLO cycle
- `POST /api/devices/{id}/restart` creates a `reboot` command
- The firmware does not currently implement all command types — `reboot` via AUTH trigger works; `config` and `ban` are reserved for future firmware support

---

### `FlowLayouts`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `Id` | int | PK, auto-increment | |
| `UserId` | int | FK → `Users.Id`, UNIQUE | One layout per user |
| `PositionsJson` | string | NOT NULL, default `"{}"` | JSON object: node ID → `{x, y}` coordinates |
| `UpdatedAt` | DateTime | NOT NULL | UTC |

**Notes:**
- Stores the `x, y` positions of DeviceNode and TemplateNode in the React Flow canvas so the layout persists across browser sessions
- `GET /api/flow-layout` returns the JSON; `POST /api/flow-layout` overwrites it (debounced in the frontend on drag-stop)

---

### `PipelineEvents`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `Id` | int | PK, auto-increment | |
| `Ts` | DateTime | NOT NULL | UTC |
| `EventType` | string | NOT NULL | e.g. `"grab_queued"`, `"udp_done"`, `"device_online"` |
| `DeviceId` | int? | NULL | FK → Devices.Id |
| `TemplateId` | int? | NULL | FK → Templates.Id |
| `SessionId` | string? | NULL | UDP session ID for correlation |
| `Success` | bool? | NULL | |
| `DurationMs` | int? | NULL | Time taken for the operation in milliseconds |
| `Payload` | string? | NULL | JSON — arbitrary extra data |
| `ErrorMessage` | string? | NULL | |

**Indexes:** `(DeviceId, Ts)`, `(TemplateId, Ts)`, `(EventType, Ts)` — for the pipeline summary view

**Event type vocabulary:**

| EventType | Source | Meaning |
|---|---|---|
| `grab_queued` | TemplatesController | User clicked Grab or worker scheduled |
| `grab_completed` | Chrome.cs | Screenshot written to ExchangeFolder |
| `grab_failed` | Chrome.cs | Chrome error or timeout |
| `udp_session_opened` | UDPServer | AUTH accepted, transfer starting |
| `udp_frame_sent` | UDPServerSession | DONE sent to device |
| `udp_not_modified` | UDPServer | NOT_MODIFIED sent (CRC match) |
| `device_online` | UDPServer | First PING after offline period |
| `device_offline` | DeviceMonitorService | No ping for timeout period |

---

### `GuestDevices`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `Id` | int | PK, auto-increment | |
| `Guid` | Guid | UNIQUE, NOT NULL | Device identity — baked into firmware |
| `TemplateName` | string | NOT NULL | Template filename or DB template name |
| `TemplateId` | int? | NULL | FK → Templates.Id; null for legacy filesystem templates |
| `DisplayWidth` | int | NOT NULL | |
| `DisplayHeight` | int | NOT NULL | |
| `CreatedAt` | DateTime | NOT NULL | UTC |
| `LastGrabbedAt` | DateTime? | NULL | UTC timestamp of most recent successful grab; null if never grabbed |

**Notes:**
- Guest devices are registered from the landing page flash wizard — no user account required
- `GuestDeviceRefreshWorker` grabs their template on the same 5-minute cycle as regular devices
- Guest devices cannot be managed from the admin dashboard — they are visible only in server logs

---

## Security database

### `SecurityEvents`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `Id` | int | PK, auto-increment | |
| `IpAddress` | string | NOT NULL | Originating IP (extracted from proxy headers) |
| `UserAgent` | string? | NULL | HTTP User-Agent header value |
| `Endpoint` | string | NOT NULL | Request path and query string |
| `Method` | string | NOT NULL | HTTP method (`GET`, `POST`, …) |
| `EventType` | string | NOT NULL | See enum below |
| `StatusCode` | int | NOT NULL | HTTP status code returned to the client |
| `RequestBody` | string? | NULL | First 500 chars of POST body; null for other methods |
| `CreatedAt` | DateTime | NOT NULL | UTC |
| `UserId` | string? | NULL | Authenticated user ID (string), if token was valid |
| `SessionId` | string? | NULL | JWT `jti` claim, if token was present |
| `Details` | string? | NULL | Free-text context (e.g. matched honeypot path) |

**Indexes:** `(IpAddress, CreatedAt)`, `(EventType, CreatedAt)`

**EventType values:**

| Value | Trigger |
|---|---|
| `BruteForce` | Repeated 401 responses on `/api/auth/login` |
| `ScannerProbe` | Request to a scanner honeypot path (`.env`, `wp-admin`, etc.) |
| `SuspiciousActivity` | Malicious User-Agent string detected |
| `RateLimit` | 429 Too Many Requests |
| `InvalidToken` | 401/403 on a protected endpoint |
| `Blocked` | Request rejected because IP is already in `BlockedIps` |

---

### `BlockedIps`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `Id` | int | PK, auto-increment | |
| `IpAddress` | string | NOT NULL, indexed | The blocked IP |
| `Reason` | string | NOT NULL | e.g. `"AutoBlock:ScannerProbe"`, `"ManualBlock"` |
| `BlockedAt` | DateTime | NOT NULL | UTC |
| `BlockedUntil` | DateTime? | NULL | null = permanent block |
| `IsManual` | bool | NOT NULL | `true` = admin action, `false` = automatic |
| `UnblockedAt` | DateTime? | NULL | Set when block is lifted |

**Notes:**
- `IsBlockedAsync(ip)` is called on every request by `SecurityMiddleware` — the `IpAddress` index is critical
- Loopback addresses (127.0.0.1, ::1) are never checked against this table — they bypass the middleware entirely
- Auto-blocks expire after a configured duration (`SecurityConfig.AutoBlockDurationHours`); manual blocks are permanent until lifted via admin API

---

### `SessionFingerprints`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `Id` | int | PK, auto-increment | |
| `UserId` | string | NOT NULL | String form of the user's integer PK from the main DB |
| `SessionId` | string | NOT NULL | JWT `jti` claim |
| `IpAddress` | string | NOT NULL | IP at login time |
| `UserAgent` | string | NOT NULL | Raw User-Agent string recorded at session creation |
| `UserAgentHash` | string | NOT NULL | SHA-256 hex digest of `UserAgent` for fast comparison |
| `CreatedAt` | DateTime | NOT NULL | UTC |
| `LastSeenAt` | DateTime | NOT NULL | UTC — updated on each authenticated request |
| `IsActive` | bool | NOT NULL | `false` = revoked |
| `RevokedAt` | DateTime? | NULL | UTC |

**Indexes:** `(UserId, SessionId)` — hot-path for token validation

**Notes:**
- `SessionFingerprintService.RecordAsync` is called on every authenticated request; if the IP or User-Agent changes mid-session, the mismatch is logged as a `SuspiciousActivity` event
- `DELETE /api/me/sessions/{sessionId}` calls `RevokeAsync` — sets `IsActive=false` and `RevokedAt`
- The JWT itself has no revocation mechanism; `SessionFingerprints` is the revocation registry

---

## Migration history

| Migration | Date | What changed |
|---|---|---|
| `20260323105025_InitialCreate` | 2026-03-23 | Users, Devices, Templates, Connections, DevicePings, GrabLogs |
| `20260323120838_AddGrabLog` | 2026-03-23 | GrabLog.TokenGuid, CompletedAt, Success, Message |
| `20260324193107_InitialSecurity` | 2026-03-24 | SecurityEvents, BlockedIps, SessionFingerprints (security DB) |
| `20260326093944_ExtendDeviceMonitoring` | 2026-03-26 | Device: DisplayType, FirmwareVersion, WifiRssi, FreeHeap, UptimeSeconds, LastSeenAt, Status, LastFrameCrc, FramesSentCount |
| `20260327120000_AddTemplateMonochrome` | 2026-03-27 | Template.Monochrome |
| `20260331175421_AddFlowLayout` | 2026-03-31 | FlowLayouts table |
| `20260407120000_AddDeviceFingerprint` | 2026-04-07 | Device.MacAddress |

---

## Repository pattern

All DB access goes through repository interfaces — no DbContext is injected directly into controllers or services.

```csharp
// Example: resolve device token to active template
Device? device = await _deviceRepo.GetByTokenAsync(token, ct);
if (device?.ActiveTemplateId is int tplId)
{
    // DisplayWidth/Height are int? — prefer resolution-specific grab when available
    GrabLog? last = device.DisplayWidth.HasValue && device.DisplayHeight.HasValue
        ? await _grabLogRepo.GetLastSuccessfulAsync(tplId, device.DisplayWidth.Value, device.DisplayHeight.Value, ct)
        : await _grabLogRepo.GetLastSuccessfulAsync(tplId, ct);
    // → last?.TokenGuid → ExchangeFolder/{guid}.bin
}
```

Repository implementations use EF Core with `AsNoTracking()` on all read-only queries and explicit `SaveChangesAsync()` on writes. No lazy loading — all navigation properties are loaded explicitly where needed.
