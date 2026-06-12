# ViewOwl

**ViewOwl puts live web pages on small dedicated screens.**

Write an ordinary HTML page — a weather widget, a transit board, a café menu, an animated sci-fi panel — and it appears on a little display hanging on your wall. No embedded programming, no re-flashing, no drawing pixels in C. If you can make a web page, you can make a device.

The screens themselves stay deliberately simple: they hold no application logic, fetch no data, and know nothing about your content. A small self-hosted server renders every page and sends ready-made pictures to each screen over Wi-Fi. Content, layout, styling, and data fetching all live in plain HTML and JavaScript on that server — edit the page, and every screen showing it follows.

## What you can build with it

- **Live data screens** — weather, air quality, exchange rates, flights overhead, the next train: anything with an API becomes a wall display in the time it takes to write the HTML.
- **Status boards** — server-rack health, CI state, a home-automation overview at a glance.
- **Menu and price boards** — edited as a web page, updated on every screen in the room at once.
- **Animated art frames** — looped animations rendered once on the server, stored inside the device, and played back forever — even with no network at all.
- **Fleets** — one server feeds many screens, each with its own content, each at its own native resolution.

ViewOwl is built for common hobbyist ESP32 display boards and stays light on purpose: the whole server side runs comfortably on a small VPS or a single-board computer — no high-performance hardware required on either end. Devices are flashed and configured straight from the browser in a couple of clicks — no toolchain, no drivers, no IDE.

---

## How it works

1. The server runs a headless Chromium instance (via PuppeteerSharp on ARM64).
2. For each registered template, Chromium loads the page, takes a screenshot, and converts it to BGR565 raw format.
3. The resulting `.bin` file is placed in a shared exchange folder.
4. The UDP server reads that file and streams it to the ESP32 in 1400-byte chunks using a simple HELLO/AUTH/DATA/ACK/DONE protocol.
5. The ESP32 receives the chunks, assembles the frame, and writes it to the LCD via SPI.

For animated templates (Class C), the server pre-renders multiple frames, compresses them, and stores them in a batch. The ESP32 writes the entire batch to its flash partition and plays it back in a loop without waiting for the network.

---

## Supported displays

| Variant | Controller | Resolution | Backlight GPIO |
|---|---|---|---|
| Default | ILI9341 | 320x240 | GPIO 21 |
| `-DLCD_480x320=1` | ST7796 | 480x320 | GPIO 27 |
| `-DLCD_ILI9486=1` | ILI9486 | 480x320 | hardwired (RPi-compatible boards) |

The firmware variant is selected at compile time. CI builds all three and publishes them as release assets.

---

## Template classes

**Class A** — a static HTML page or external URL. Chromium loads it, takes one screenshot, sends it to the device. Suitable for dashboards, weather, prices.

**Class B** — same as A but the page is rendered at the exact resolution of each connected device. If two different displays are connected to one template, each gets a screenshot at its own resolution.

**Class C** — animated. The server pre-renders a sequence of frames (up to 16), compresses each one, and sends the whole batch to the device. The device stores it in flash and plays it back in a loop at a defined FPS, independently of the network.

---

## Components

```
ViewOwl/
├── ViewOwl.Grabber.WebAPI/     ASP.NET Core 8 — web server, admin dashboard, template management,
│   ├── ClientApp/              React admin SPA (Vite, served from /dashboard/)
│   ├── Controllers/            REST API for templates, devices, grabs, push
│   ├── BackgroundServices/     GrabberWorker (periodic grab), DeviceTemplateRefreshWorker (Class C render),
│   │                           GuestDeviceRefreshWorker, DbCleanupWorker
│   └── wwwroot/                Landing page (index.html), legal pages, firmware binaries (/firmware/v1/)
│
├── ViewOwl.UDP.Server/         .NET 8 console — listens on UDP 11000, manages per-device sessions,
│                               implements HELLO/AUTH/DATA/ACK/DONE/PING/BATCH protocol
│
├── ViewOwl.Grabber.Engine/     PuppeteerSharp wrapper — launches ARM64 Chromium, takes screenshots,
│                               converts PNG to BGR565 .bin
│
├── ViewOwl.ESP32.Client/       ESP-IDF 5.5 firmware (C99)
│   └── main/
│       ├── udp_client.c        protocol state machine, batch transfer, flash playback
│       ├── lcd_init.c          SPI display init (ILI9341 / ST7796 / ILI9486)
│       ├── flash_frames.c      flash partition read/write for Class C animation
│       ├── nvs_storage.c       NVS persistence (WiFi credentials, batch CRC)
│       └── config.h            compile-time constants, display variant selection
│
├── ViewOwl.UDP.Utils/          Shared C# packet structs (mirrored in packet.h for the firmware)
├── ViewOwl.Config/             Shared configuration models
├── ViewOwl.Data/               EF Core — SQLite, repositories, migrations
│
├── ExchangeFolder/
│   └── SitesTemplates/         HTML templates served to Chromium via HTTP
│
└── .github/workflows/
    └── docs.yml                Builds and publishes the documentation site
```

---

## Local development

See **[docs/local-development.md](docs/local-development.md)** for the full walkthrough.

Short version (Windows):

```powershell
# 1. Download Chrome for Testing (one-time setup)
.\setup-chrome.ps1

# 2. Edit appsettings.json — set a real JWT secret and admin password

# 3. Start the API server
dotnet run --project ViewOwl.Grabber.WebAPI
# → http://localhost:5085  (Swagger UI: /swagger)

# 4. (Optional) React dashboard with hot reload
cd ViewOwl.Grabber.WebAPI\ClientApp
npm install && npm run dev
# → http://localhost:5173/dashboard/
```

---

## Deployment

To run ViewOwl on your own server, publish the two .NET projects as
self-contained `linux-arm64` (or `linux-x64`) binaries, put them behind a
reverse proxy that terminates TLS (e.g. Caddy), and run them as services.

The full step-by-step walkthrough — building, systemd units, reverse proxy,
firmware hosting — is in **[Self-hosting](docs/server/self-hosting.md)**.

---

## Flashing the firmware

Devices are flashed from the browser using [esp-web-tools](https://esphome.github.io/esp-web-tools/) (Web Serial API). The landing page at the server domain provides a flash wizard that selects the correct binary based on the display the user chooses. No drivers or local toolchain required.

After flashing, the firmware opens a serial terminal for Wi-Fi provisioning. Credentials are written to NVS and survive OTA updates.

To build locally:

```bash
cd ViewOwl.ESP32.Client
idf.py build                        # ILI9341 320x240
idf.py -DLCD_480x320=1 build        # ST7796 480x320
idf.py -DLCD_ILI9486=1 build        # ILI9486 480x320
```

---

## Configuration

Before flashing, set your credentials in `ViewOwl.ESP32.Client/main/config.h`:

```c
#define WIFI_SSID   "YOUR_WIFI_SSID"
#define WIFI_PASS   "YOUR_WIFI_PASSWORD"
#define SERVER_IP   "YOUR_SERVER_IP"
#define SERVER_PORT 11000
```

Each firmware variant has a unique device token (`TOKEN` / `TOKEN_BYTES`) that identifies it to the server. If you are running multiple devices, assign a different GUID per device.

---

## Documentation

| Document | What it covers |
|---|---|
| [Local development](docs/local-development.md) | Running the server locally, Chrome setup, hot reload |
| [Getting started](docs/getting-started.md) | First device setup, flashing, Wi-Fi provisioning |
| [Hardware guide](docs/hardware.md) | Supported displays, wiring, SPI pinout, memory constraints |
| [Architecture](docs/architecture.md) | How Grabber, UDP Server, and firmware fit together |
| [Templates overview](docs/templates/overview.md) | `data-vow-*` attributes, the Class A/B/C system |
| [Class A templates](docs/templates/class-a.md) | Static and API-driven HTML pages |
| [Class B templates](docs/templates/class-b.md) | Canvas-based templates, browser push mode |
| [Class C templates](docs/templates/class-c.md) | Multi-frame animations stored in ESP32 flash |
| [Firmware reference](docs/reference/firmware.md) | Modules, config constants, build variants |
| [UDP transport](docs/reference/udp-transport.md) | Full protocol spec, packet layout, edge cases |
| [Renderer pipeline](docs/reference/renderer.md) | Chromium to BGR565 conversion |
| [Self-hosting](docs/server/self-hosting.md) | Deploying to your own server |
| [API reference](docs/reference/api-reference.md) | REST endpoints |
| [Auth & security](docs/reference/auth-security.md) | JWT, admin access, IP blocking |

---

## License

MIT — see [License.md](License.md).

The core system concept (browser-rendered content delivery to resource-constrained devices via a server-side format broker) is dedicated to the public domain via defensive publication:
DOI [10.5281/zenodo.20325797](https://doi.org/10.5281/zenodo.20325797)

---

## Community

- GitHub: https://github.com/KirinDenis/ViewOwl_ESP32
- Facebook: https://www.facebook.com/groups/OWLOS
