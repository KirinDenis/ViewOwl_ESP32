# Architecture

ViewOwl has three components that run independently and communicate through the filesystem and UDP:

1. **Grabber** (`ViewOwl.Grabber.WebAPI`) — ASP.NET Core 8 service. Runs headless Chromium, renders HTML templates to BGR565 `.bin` files, serves templates over HTTP so Chromium can load them.
2. **UDP Server** (`ViewOwl.UDP.Server`) — .NET 8 console app. Listens for ESP32 HELLO packets, streams `.bin` files to devices over a custom ACK protocol.
3. **ESP32 firmware** (`ViewOwl.ESP32.Client`, plus `ViewOwl.ESP32_C3.Client` for the round display) — ESP-IDF C firmware. Connects to WiFi, requests frames from the UDP server, drives the LCD. The round 240×240 GC9A01 panel runs a separate ESP32-C3 client that shares the SoC-agnostic logic and the identical wire protocol.

Supported display types are described once in a root **`display-types.json`** registry (`firmwareFamilies` + `displayTypes`), which drives the server's per-type firmware version checks, the CI firmware build, and the browser flasher manifests — adding a display is one registry entry, no drift across those layers.

---

## End-to-end data flow

```
HTML template (DB or URL)
        │
        ▼
 SiteTemplateController          Chrome.cs (headless Chromium, ARM64)
 GET /SiteTemplate?name=*.html ──► navigates tab → PNG screenshot
        │                                │
        │  (Chromium can't open          ▼
        │   file:// on ARM64)     resize → BGR565 .bin
        │                                │
        └────────────────────────────────┘
                                         │
                               ExchangeFolder/{guid}.bin
                                         │
                                         ▼
                              ViewOwl.UDP.Server (port 11000/UDP)
                              ┌────────────────────────────────┐
                              │ HELLO  ← device sends GUID     │
                              │ AUTH   → server sends session  │
                              │ DATA   → server streams .bin   │
                              │ ACK    ← device confirms chunk │
                              │ DONE   → transfer complete     │
                              └────────────────────────────────┘
                                         │ UDP 11000
                              ┌──────────┴──────────┐
                              │                     │
                         ESP32 + LCD
                         (firmware)
```

---

## Component details

### ViewOwl.Grabber.WebAPI

**Entry point:** `Program.cs`  
**Port:** 5000/TCP (configurable via `appsettings.json`)

Key background services:

| Service | File | What it does |
|---|---|---|
| `DeviceTemplateRefreshWorker` | `BackgroundServices/DeviceTemplateRefreshWorker.cs` | Periodically re-grabs every template at every connected device's resolution. Class C: multi-frame grab. Class A/B: single screenshot. |
| `GuestDeviceRefreshWorker` | `BackgroundServices/GuestDeviceRefreshWorker.cs` | Same as above but for unauthenticated guest devices registered from the landing page. |
| `GrabberWorker` | `BackgroundServices/GrabberWorker.cs` | Legacy: grabs external URLs listed in `GrabbedList.json`. |
| `DbCleanupWorker` | `BackgroundServices/DbCleanupWorker.cs` | Prunes stale `.bin` / `.png` files and VACUUMs the SQLite database. |

Key controllers:

| Controller | Route | Purpose |
|---|---|---|
| `SiteTemplateController` | `GET /SiteTemplate` | Serves HTML templates from DB or filesystem over HTTP so Chromium can load them. |
| `GuestDeviceController` | `POST /api/guest/device` | Registers unauthenticated guest devices; accepts pushed BGR565 frames. |

**Why HTTP instead of `file://`?**  
Headless Chromium on Linux ARM64 blocks `file://` access by default. All local templates are served through `SiteTemplateController` as `http://localhost:5000/SiteTemplate?name=...` so Chromium can load them normally.

---

### ViewOwl.UDP.Server

**Entry point:** `UDPServerProgram.cs`  
**Port:** 11000/UDP

Each device connection spawns a `UDPServerSession` which handles the full handshake and file transfer. Sessions are short-lived — one per frame delivery cycle.

**NOT_MODIFIED optimisation:** If the device's reported CRC matches the current batch CRC stored in the manifest, the server responds with `NOT_MODIFIED` and skips retransmission. This prevents re-flashing unchanged content on every HELLO cycle.

See [`UDPServer.cs`](../ViewOwl.UDP.Server/UDPServer.cs) and [`UDPServerSession.cs`](../ViewOwl.UDP.Server/UDPServerSession.cs).

---

### ESP32 Firmware

**Language:** C99, ESP-IDF 5.5  
**Entry:** `main/main.c`

Key modules:

| File | Responsibility |
|---|---|
| `udp_client.c` | HELLO/AUTH/DATA/ACK/DONE protocol, batch flash write, NVS CRC persistence |
| `lcd_init.c` | SPI bus + display controller initialisation |
| `nvs_storage.c` | NVS read/write for WiFi credentials, device token, batch CRC |
| `config.h` | All compile-time constants: pins, resolution, server IP, thresholds |

---

## Packet protocol

All UDP communication uses a 15-byte binary header defined in [`UDPUtils.cs`](../ViewOwl.UDP.Utils/UDPUtils.cs) (C#) and mirrored in [`packet.h`](../ViewOwl.ESP32.Client/main/packet.h) (C). Both sides **must stay in sync**.

```
PacketHeader (15 bytes, LayoutKind.Sequential Pack=1):
  UInt32  Magic         0xABADBABE  — drops alien packets
  byte    PacketType                — HELLO/AUTH/DATA/ACK/DONE/ERROR
  UInt16  SessionId                 — assigned at AUTH
  UInt32  Seq                       — chunk index (DATA/ACK)
  UInt16  PayloadLength             — bytes in this packet
  UInt16  Flags                     — LAST | RETRANSMIT | TIMEOUT | BADPACKET | BYE
```

See [`docs/hardware.md`](hardware.md) for packet size rationale.

---

## ExchangeFolder

The only shared state between the Grabber and the UDP Server is the filesystem:

```
ExchangeFolder/
├── {guid}.bin          BGR565 raw frame — written by Grabber, read by UDP Server
├── {guid}.png          Full-colour preview — written by Grabber, served by API
├── {guid}_batch.json   Class C manifest (frame count, fps, batch CRC)
├── t{id}_{w}x{h}.htmlhash   SHA256 of template HTML — skip re-render if unchanged
└── SitesTemplates/     HTML templates served to Chromium
```

Both processes run on the same machine. The Grabber writes atomically (`.tmp` → rename) so the UDP Server never reads a partial file.

---

## C# project dependency graph

```
ViewOwl.Config
    ▲
    ├── ViewOwl.UDP.Utils
    │       ▲
    │       └── ViewOwl.UDP.Server
    │
    ├── ViewOwl.Grabber.DTO
    │       ▲
    │       └── ViewOwl.Grabber.Engine
    │               ▲
    │               └── ViewOwl.Grabber.WebAPI
    │
    └── (ViewOwl.Grabber.WebAPI also refs ViewOwl.Config directly)
```

---

## Deployment

Both server components publish as `linux-arm64` self-contained single-file binaries. A CI pipeline builds them and deploys to a Linux ARM64 server over SSH.

See [Self-hosting](server/self-hosting.md) for manual deployment instructions.
