# Authentication & Security Reference

**Projects involved:**  
- [`ViewOwl.Security/`](../../ViewOwl.Security/) — IP blocking, session fingerprinting, security event logging  
- [`ViewOwl.Grabber.WebAPI/`](../../ViewOwl.Grabber.WebAPI/) — JWT auth, middleware pipeline, controllers

---

## Authentication

### JWT flow

```
POST /api/auth/login
  Body: { "login": "admin", "password": "..." }

  1. IUserRepository.GetByLoginAsync(login)
  2. BCrypt.Verify(password, user.PasswordHash)  ← timing-safe comparison
  3. Generate JWT:
       sub  = userId (string)
       name = login
       role = "Admin" | "User"
       jti  = Guid.NewGuid()  ← session ID for fingerprinting and revocation
       exp  = now + JwtConfig.ExpiryMinutes
  4. ISessionFingerprintService.RecordAsync(userId, jti, HttpContext)
       ← records IP + UserAgent hash in SessionFingerprints table
  5. Return: { "token": "eyJ..." }
```

**On failure:** always returns HTTP 401 with a generic message — no indication whether login or password was wrong. Prevents user enumeration.

**JWT config** (from `appsettings.json` → `IOptions<JwtConfig>`):

| Field | Notes |
|---|---|
| `Secret` | HMAC-SHA256 signing key — minimum 32 chars |
| `Issuer` | Must match `ValidIssuer` in middleware |
| `Audience` | Must match `ValidAudience` |
| `ExpiryMinutes` | Token lifetime; default 1440 (24 h) |

### JWT validation middleware

All routes except `/api/auth/login`, `/api/public-templates`, `/SiteTemplate`, `/health`, and `/api/guest/*` require a valid Bearer token.

Validation checks: signature, issuer, audience, expiry, and that the `jti` exists in `SessionFingerprints` with `IsActive=true`.

### Role-based access

| Role | Access |
|---|---|
| `Admin` | All endpoints, including `AdminUsersController` and `SecurityDashboardController` |
| `User` | Own devices, own templates, own connections only |

Controllers use `[Authorize(Roles = "Admin")]` or `[Authorize]` + manual ownership check (e.g., `device.UserId != currentUserId → 403`).

---

## User management

**Controller:** [`AdminUsersController.cs`](../../ViewOwl.Grabber.WebAPI/Controllers/AdminUsersController.cs)  
**Required role:** Admin

### Endpoints

| Method | Route | Body / Notes |
|---|---|---|
| `GET` | `/api/admin/users` | Returns all users (login, role, sandboxEnabled, createdAt) |
| `POST` | `/api/admin/users` | `{ login, password, role, sandboxEnabled }` — BCrypt-hashed at creation |
| `DELETE` | `/api/admin/users/{id}` | Cascades: deletes user's devices, templates, connections |
| `PATCH` | `/api/admin/users/{id}/sandbox` | `{ enabled: true/false }` — toggles preview iframe sandbox |

**Password storage:** BCrypt with work factor 12. The raw password is never logged or stored.

### Current user profile

**Controller:** [`MeController.cs`](../../ViewOwl.Grabber.WebAPI/Controllers/MeController.cs)

| Method | Route | Notes |
|---|---|---|
| `GET` | `/api/me` | `{ id, login, role, sandboxEnabled }` |
| `GET` | `/api/me/sessions` | All active sessions with IP, UserAgent, createdAt, isCurrent |
| `DELETE` | `/api/me/sessions/{sessionId}` | Revoke a specific session (remote logout) |
| `DELETE` | `/api/me/sessions` | Revoke all sessions except the current one |

---

## Session fingerprinting

**Service:** [`ViewOwl.Security/Services/SessionFingerprintService.cs`](../../ViewOwl.Security/Services/SessionFingerprintService.cs)

On each authenticated request, the service:
1. Extracts the `jti` from the JWT
2. Looks up the `SessionFingerprint` record
3. Compares current IP and `SHA256(User-Agent)` to the stored values
4. If either changes → logs a `SuspiciousActivity` security event
5. Updates `LastSeenAt`

This detects token theft — if a stolen JWT is used from a different IP or device, the mismatch is logged and can trigger an automatic block.

**Methods:**

| Method | Purpose |
|---|---|
| `RecordAsync(userId, jti, ctx)` | Create or refresh session on login |
| `GetActiveSessionsAsync(userId)` | List all active sessions for `/api/me/sessions` |
| `ValidateAsync(userId, jti, ctx)` | Check fingerprint on each request |
| `RevokeAsync(userId, jti)` | Set `IsActive=false, RevokedAt=now` |
| `RevokeAllAsync(userId, excludeJti)` | Bulk revoke (log out all other devices) |

---

## Security middleware pipeline

**File:** [`ViewOwl.Grabber.WebAPI/Middleware/SecurityMiddleware.cs`](../../ViewOwl.Grabber.WebAPI/Middleware/SecurityMiddleware.cs)

Runs on **every request**, before routing and authentication. Order matters:

```
1. Extract real client IP
   X-Forwarded-For → X-Real-IP → RemoteIpAddress
   (supports reverse proxy deployments)

2. Loopback bypass
   127.0.0.1 / ::1 → skip all checks, proceed immediately
   (internal service calls from UDP Server must not be blocked)

3. IP block check
   IpBlockService.IsBlockedAsync(ip) → HTTP 403 if blocked
   Logs SecurityEvent(Blocked) on hit

4. Scanner honeypot detection
   Path matches any of:
     /.env, /.git, /wp-admin, /wp-login.php, /phpmyadmin,
     /actuator, /manager/html, /admin.php, /.DS_Store,
     /etc/passwd, /proc/self, /xmlrpc.php, /config.php, ...
   → HTTP 404 + auto-block IP + log SecurityEvent(ScannerProbe)

5. Malicious User-Agent detection
   User-Agent contains: sqlmap, nikto, masscan, nmap, zgrab,
     nuclei, dirbuster, gobuster, wfuzz, hydra, metasploit, ...
   → HTTP 400 + log SecurityEvent(SuspiciousActivity)
   Exception: curl with Accept headers is allowed (dev/testing)

6. Security response headers (added to all responses)
   X-Content-Type-Options: nosniff
   X-Frame-Options: SAMEORIGIN
   X-XSS-Protection: 1; mode=block
   Referrer-Policy: strict-origin-when-cross-origin
   Permissions-Policy: camera=(), microphone=(), geolocation=()

7. Pass to next middleware

8. Post-response logging (after response completes)
   Status 401 → SecurityEvent(InvalidToken)
   Status 429 → SecurityEvent(RateLimit)
   (logged after response so status code is known)
```

---

## IP block service

**Service:** [`ViewOwl.Security/Services/IpBlockService.cs`](../../ViewOwl.Security/Services/IpBlockService.cs)

### Methods

| Method | Notes |
|---|---|
| `IsBlockedAsync(ip)` | Hot-path — indexed query on `BlockedIps.IpAddress`; returns `false` for loopback |
| `BlockAsync(ip, reason, isManual, expiresAt)` | Upsert: creates or updates existing block; `isManual=true` for admin action |
| `UnblockAsync(ip)` | Sets `UnblockedAt=now`; does not delete the record (audit trail preserved) |
| `AutoBlockIfNeededAsync(ip, recentEvents)` | Called after each `ScannerProbe` event; blocks if threshold exceeded within window |
| `GetBlockedAsync()` | Admin list — all currently active blocks |
| `GetHistoryAsync(ip)` | All block/unblock events for a given IP |

### Auto-block thresholds (from `SecurityConfig`)

| Trigger | Threshold | Action |
|---|---|---|
| `ScannerProbe` events | ≥ 1 (immediate) | Block for `AutoBlockDurationHours` |
| `BruteForce` events | ≥ 5 in 10 min | Block for `AutoBlockDurationHours` |
| `SuspiciousActivity` | ≥ 1 (immediate) | Block for `AutoBlockDurationHours` |

---

## Localhost-only middleware

**File:** [`ViewOwl.Grabber.WebAPI/Middleware/LocalhostOnlyMiddleware.cs`](../../ViewOwl.Grabber.WebAPI/Middleware/LocalhostOnlyMiddleware.cs)

All routes under `/api/internal/*` are rejected with HTTP 403 from any IP other than `127.0.0.1` / `::1`.

This protects the internal API (used by the UDP Server) from external access. The UDP Server runs on the same machine as the Grabber API and calls these endpoints over loopback.

```
Incoming request to /api/internal/*
  RemoteIP == 127.0.0.1 or ::1 → proceed
  else → HTTP 403 "Internal API is accessible from localhost only."
```

---

## API logging middleware

**File:** [`ViewOwl.Grabber.WebAPI/Middleware/ApiLoggingMiddleware.cs`](../../ViewOwl.Grabber.WebAPI/Middleware/ApiLoggingMiddleware.cs)

Logs all `/api/*` requests:

```
[2026-05-17 14:23:01] GET /api/devices by userId=3 → 200 OK in 12ms
[2026-05-17 14:23:02] POST /api/templates by userId=3 → 201 Created in 45ms
```

Skipped for: static files, SignalR WebSocket upgrade, `/health`.

---

## Security event dashboard

**Controller:** `SecurityDashboardController` (Admin-only)

| Endpoint | Returns |
|---|---|
| `GET /api/admin/security/events` | Recent `SecurityEvent` records, filterable by type and IP |
| `GET /api/admin/security/blocked` | All currently active IP blocks |
| `POST /api/admin/security/block` | Manually block an IP |
| `DELETE /api/admin/security/block/{ip}` | Lift a block |
| `GET /api/admin/security/sessions` | All active sessions across all users |

---

## Frame push rate limiting

**Service:** `IFramePushThrottle`

`POST /api/templates/{id}/frame` (browser → server frame push) is rate-limited to **60 fps** — minimum 16ms between frames from the same client. The throttle is per-user, tracked in memory.

Requests arriving faster than 60 fps receive HTTP 429. This prevents a runaway browser push loop from saturating the server.

---

## Sandbox mode

When `User.SandboxEnabled = true`, the admin panel's template preview iframe gets the HTML `sandbox` attribute. This restricts JS execution in the preview panel only — it does **not** affect:
- Server-side grabs (Chromium runs without sandbox restrictions)
- Device rendering

Use case: untrusted template authors who need to preview their work in the admin panel without being able to run arbitrary JS in the admin's browser.
