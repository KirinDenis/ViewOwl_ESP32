# Local Development Guide

This guide walks through running ViewOwl on a Windows development machine from a fresh clone.
Running it on a Linux server is covered separately in [Self-hosting](server/self-hosting.md).

---

## Prerequisites

| Tool | Minimum version | Check |
|---|---|---|
| .NET SDK | 8.0 | `dotnet --version` |
| Node.js | 18 | `node --version` |
| npm | 9 | `npm --version` |
| Git | any | `git --version` |

---

## 1. Download Chrome for Testing (Windows only)

ViewOwl renders HTML templates using a pinned build of Chrome for Testing.
The binary is **not bundled in git** — download it once before the first run:

```powershell
# Run from the solution root
.\setup-chrome.ps1
```

This downloads Chrome **143.0.7499.169** (~300 MB) and extracts it to
`ViewOwl.Grabber.Engine\Chrome-for-testing\Chrome-win64\`.

> **Linux / macOS:** Use `./setup-chrome.sh` instead. It installs the system
> `chromium-browser` package via `apt` or `brew`.

If Chrome is missing when the server starts, a visible error banner is printed to the
console with the exact path and fix command — you cannot miss it.

---

## 2. Configure secrets

Open `ViewOwl.Grabber.WebAPI/appsettings.json` and replace the placeholder values:

```json
"Jwt": {
  "Secret": "CHANGE_THIS_SECRET_MIN_32_CHARS_LONG",   // ← replace with a random 32+ char string
  ...
},
"AdminSeed": {
  "Login": "admin",
  "Password": "CHANGE_THIS_ADMIN_PASSWORD"    // ← replace with a strong password
}
```

> The database and admin user are created automatically on first startup — no manual
> migration step needed.

---

## 3. Start the API server

```powershell
dotnet run --project ViewOwl.Grabber.WebAPI
```

The server starts on **http://localhost:5085** (HTTP) and **https://localhost:7052** (HTTPS).

On first run:
- EF Core migrations are applied automatically.
- The admin user from `AdminSeed` is seeded.
- All `*.html` files in `ExchangeFolder/SitesTemplates/` are seeded as templates.
- The grab worker starts grabbing every template in the background.

**Swagger UI** (available in Development only):

```
http://localhost:5085/swagger
```

Authenticate via `POST /api/auth/login`, copy the returned token, click **Authorize** in
Swagger UI, and paste the token — no `Bearer` prefix needed.

---

## 4. Start the React dashboard (optional — hot reload)

The production dashboard is pre-built into `wwwroot/dashboard/` and served by .NET.
For frontend development with hot reload, start the Vite dev server separately:

```powershell
cd ViewOwl.Grabber.WebAPI\ClientApp
npm install        # first time only
npm run dev
```

Open **http://localhost:5173/dashboard/** — the browser redirects to the login page
(proxied from .NET), log in, and the dashboard loads with instant hot reload on every
`.jsx` / `.css` change.

> The Vite dev server proxies `/api`, `/hubs`, `/login.html`, and `/js/` to port 5085,
> so the .NET server must be running at the same time.

---

## 5. Start the UDP server (optional — device streaming)

The UDP server is only needed when testing actual ESP32 hardware:

```powershell
dotnet run --project ViewOwl.UDP.Server
```

It listens on **UDP port 11000** and streams BGR565 frames to connected devices.

---

## Port summary

| Service | URL | Notes |
|---|---|---|
| API server (HTTP) | http://localhost:5085 | primary dev URL |
| API server (HTTPS) | https://localhost:7052 | optional |
| Swagger UI | http://localhost:5085/swagger | Development only |
| Admin dashboard (built) | http://localhost:5085/dashboard/ | served by .NET |
| React dev server | http://localhost:5173/dashboard/ | hot reload |
| UDP server | udp://localhost:11000 | device streaming |

---

## Common issues

### Chrome banner on startup

```
!! CHROMIUM NOT FOUND — ViewOwl cannot render templates !!
```

Run `.\setup-chrome.ps1` from the solution root and restart the server.

### Templates not appearing

Templates are seeded from `ExchangeFolder/SitesTemplates/*.html` on the **first** startup
(or when new files are added). Check `GET /api/templates` via Swagger to see what was
seeded. Grabs appear within the first grab cycle (~5 minutes).

### ERR_CONNECTION_REFUSED on template pages

The server uses `Shared.WebApiBaseUrl` to load template pages into Chrome.
`appsettings.Development.json` overrides this to `http://localhost:5085` automatically —
no manual change needed in Development mode.

### Swagger returns 500

Ensure `ASPNETCORE_ENVIRONMENT=Development` is set (it is set automatically by
`launchSettings.json` when using `dotnet run`).
