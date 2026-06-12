# 🦉 ViewOwl

> **Turn any API data source into a live ESP32 display — without writing firmware**

[![Platform](https://img.shields.io/badge/platform-ESP32-blue)](https://www.espressif.com/en/products/socs/esp32)
[![Server](https://img.shields.io/badge/server-.NET%208-purple)](https://dotnet.microsoft.com)
[![License](https://img.shields.io/badge/license-MIT-green)](License.md)

*Part of the [OWLOS](https://github.com/KirinDenis/owlos) open-source IoT family (ESP32 / ESP8266 firmware & tools) — ViewOwl is its next step, not a from-scratch experiment.*

---

## In one sentence

You write a regular HTML page that pulls data from any API. ViewOwl takes a screenshot of it and streams the image to a cheap ESP32 display over Wi-Fi. That's it.

---

## What problem does this solve?

### Without ViewOwl

Say you want to show your weather station temperature on a small screen in the hallway. Here's what you'd have to do:

- Write C/C++ firmware for ESP32 that talks to your specific API
- Handle JSON parsing on a microcontroller
- Draw the UI pixel-by-pixel using a graphics library
- Re-flash the device every time you change the design
- Repeat all of this for each new data source or display type

**This is hard, slow, and requires deep embedded development knowledge.**

### With ViewOwl

1. Take any data available over HTTP/API (weather, stock prices, smart home, telemetry — anything)
2. Build an HTML page as you normally would — with CSS, animations, whatever design you like
3. Host it on a server (or locally)
4. ViewOwl automatically takes screenshots and streams them to the ESP32

**The ESP32 just receives the image and shows it. No custom firmware per use case.**

---

## Who is this for?

| You are... | ViewOwl helps you... |
|---|---|
| Building a home weather station | Display a beautiful dashboard from Open-Meteo API without any ESP32 code |
| Monitoring servers or equipment | Put Grafana / Prometheus status on a physical screen |
| Running a café or office | Hang an info board with a menu, schedule, or announcements |
| Working with smart home systems | Visualize Home Assistant data on a wall-mounted display |
| Trading crypto | Show live prices on a bedside screen |
| A maker / hobbyist | Build something cool without learning embedded GUI frameworks |

---

## Real-world examples

### Weather station

```
API: open-meteo.com (free, no key required)
Shows: temperature, humidity, pressure, forecast
Updates: every 5 minutes automatically
```

Write an HTML page that does a `fetch()` to Open-Meteo on load — and you get a beautiful live widget. ViewOwl renders it and sends it to the screen. A [ready-made template](ExchangeFolder/SitesTemplates/) is already included in the repo.

---

### Server monitoring

```
API: your Prometheus, Grafana, Zabbix, or any monitoring tool
Shows: CPU, RAM, uptime, alerts
Updates: in real time
```

Already have a dashboard in your browser? Just give ViewOwl its URL — and it will appear on a physical screen in your server room.

---

### Stock & crypto prices

```
API: CoinGecko, Yahoo Finance, any broker API
Shows: prices, charts, daily changes
Updates: every minute
```

---

### Smart home

```
API: Home Assistant REST API, MQTT-to-HTTP bridge
Shows: room temperatures, device states, energy usage
Updates: on state change
```

---

### Info board / announcements

```
Source: Google Sheets, Notion API, or plain static HTML
Shows: café menu, class schedule, office announcements
Updates: you edit the spreadsheet — the screen updates itself
```

---

## How it works

```
┌─────────────────────────────────────────────────────────────────┐
│                        YOUR HTML                                │
│  <html> + CSS + JavaScript + fetch("any API") = beautiful UI    │
└──────────────────────────────┬──────────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│                    GRABBER (server, .NET 8)                     │
│  • Opens your URL in a headless browser                         │
│  • Takes a screenshot with full rendering                       │
│  • Serves the PNG via HTTP                                      │
└──────────────────────────────┬──────────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│                    UDP SERVER (.NET 8)                          │
│  • Receives PNG from Grabber                                    │
│  • Converts to BGR565 (native format for TFT displays)          │
│  • Compresses and broadcasts to all connected clients via UDP   │
└──────────────────────────────┬──────────────────────────────────┘
                               │ UDP / Wi-Fi
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│                    ESP32 + LCD DISPLAY                          │
│  • Receives the compressed frame                                │
│  • Decompresses it                                              │
│  • Draws on screen (ST7796 / ILI9341 and others)                │
│  • Waits for the next frame                                     │
└─────────────────────────────────────────────────────────────────┘
```

**As a user, only the first and last parts matter:**
- You write HTML take an ESP32 with a display connect to the network done.

---

## Smarter than it sounds — not just screenshots

ViewOwl doesn't blast raw images over the air. Each frame is **compressed** before it's sent, and a frame that **hasn't changed isn't sent at all** — the device tells the server what it already has, and the server skips it. Animations are pre-rendered once on the server, stored in the device's flash, and **play back locally even with no network**. That's how a wall of screens stays light on a small server and an ordinary Wi-Fi connection.

---

## Why a custom protocol (and not MQTT / WebSocket)?

ViewOwl streams full-screen frames — hundreds of kilobytes each — to microcontrollers with only a few hundred kilobytes of RAM. That workload breaks the usual IoT answers:

- **MQTT** is built for small pub/sub telemetry, not bulk binary frames. Pushing big images through a broker fights payload limits and buffering, adds a broker as an extra moving part, and the common ESP32 MQTT clients are fragile over multi-day uptimes.
- **WebSocket / TCP** carry connection state and socket buffers the device can't spare — buffering and head-of-line blocking cost RAM a frame-pusher doesn't have.
- Neither gives **frame dedup or atomic offline animation** out of the box — you'd end up rebuilding exactly this on top of them.

So ViewOwl speaks a small, purpose-built protocol over UDP: a fixed-size, one-packet-at-a-time `HELLO / AUTH / DATA / ACK / DONE` flow with retransmit and session recovery. The device never holds more than a single packet in memory, reports its last frame's checksum so the server can skip unchanged frames, and keeps animating from flash if Wi-Fi drops. Devices run for **weeks** without watchdog reboots.

It isn't a shortcut around MQTT — it's the part that makes streaming display frames to a cheap microcontroller actually reliable.

---

## Built as a platform — run lean on purpose

ViewOwl is more than a renderer for one screen. **User accounts and roles already work today**, alongside a real-time management dashboard, guest devices, and built-in security (rate limiting, IP auto-blocking). That's why the server does more than a lone weather clock strictly needs — it's designed to host many people's devices, not one.

We deliberately run the live instance on **modest hardware**. It's a standing stress test: pushing a small box on purpose surfaces limits early and keeps the system honest about what it can take. The heavier stack pays off the moment you run a fleet, let several people manage their own screens, or change content as often as you edit a web page. For a single static screen it's more than you need — but it still runs on a small VPS or single-board computer.

---

## Quick start

### What you need

**Hardware:**
- ESP32 with an LCD display (ST7796 or ILI9341, 480×320 or 320×240)
- 5V power supply
- Wi-Fi network

**Software:**
- .NET 8 SDK
- ESP-IDF (to flash the ESP32)

---

### Step 1 — Prepare your HTML page

Create a regular HTML page that fetches data from your API.

```html
<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8">
  <style>
    body {
      width: 480px; height: 320px; /* match your display resolution */
      background: #0a0a1a;
      color: #00ff88;
      font-family: monospace;
      display: flex; align-items: center; justify-content: center;
    }
    .temp { font-size: 72px; font-weight: bold; }
  </style>
</head>
<body>
  <div class="temp" id="temp">--°C</div>
  <script>
    async function update() {
      const r = await fetch('https://api.open-meteo.com/v1/forecast?latitude=50.45&longitude=30.52&current=temperature_2m');
      const d = await r.json();
      document.getElementById('temp').textContent = d.current.temperature_2m + '°C';
    }
    update();
    setInterval(update, 60000); // refresh every minute
  </script>
</body>
</html>
```

> The page dimensions must match your display resolution. The standard is 480×320 px.

---

### Step 2 — Start the server

```bash
# Clone the repository
git clone https://github.com/KirinDenis/ViewOwl_ESP32.git
cd ViewOwl_ESP32

# Start the Grabber (renders your HTML to PNG)
dotnet run --project ViewOwl.Grabber.WebAPI

# In another terminal, start the UDP Server
dotnet run --project ViewOwl.UDP.Server
```

> UDP Server listens on port **11000** by default.

---

### Step 3 — Configure the ESP32

Open `ViewOwl.ESP32.Client/main/config.h` and fill in your settings:

```c
#define WIFI_SSID     "YourWiFi"
#define WIFI_PASS     "YourPassword"
#define SERVER_IP     "192.168.1.100"   // IP of the machine running UDP Server
#define SERVER_PORT   11000
```

Flash the firmware via ESP-IDF:

```bash
cd ViewOwl.ESP32.Client
idf.py build flash monitor
```

---

### Step 4 — Done!

Power on the ESP32. It will connect to Wi-Fi, receive the first frame from the server, and show your HTML page on the display.

---

## Project structure

```
ViewOwl/
├── ViewOwl.UDP.Server/          # UDP server — receives PNG, broadcasts frames
├── ViewOwl.UDP.Client.WPF/      # WPF client for Windows (debug / simulation)
├── ViewOwl.Grabber.WebAPI/      # Grabber — renders HTML to PNG via headless browser
├── ViewOwl.Grabber.Engine/      # Rendering engine (PuppeteerSharp / Chromium)
├── ViewOwl.ESP32.Client/        # ESP32 firmware (ESP-IDF, C)
├── ViewOwl.Config/              # Shared configuration
├── ViewOwl.UDP.Utils/           # UDP protocol utilities
└── ExchangeFolder/
    └── SitesTemplates/             # Ready-made HTML templates (weather, dashboards)
```

---

## Ready-made templates

The `ExchangeFolder/SitesTemplates/` folder includes templates you can use right away:

| Template | Description | API |
|---|---|---|
| `scifi_weather.html` | Sci-fi weather widget | Open-Meteo (free) |
| `scifi_weather_dashboard.html` | 3-city weather dashboard | Open-Meteo (free) |
| `scifi_weather_dashboard2.html` | Extended weather dashboard | Open-Meteo (free) |
| `scifi_weather_3.html` | Full sensor dashboard: pressure, air quality, forecast | Open-Meteo (free) |

All templates work **without API keys** and are ready to use out of the box.

---

## Technical specs

| Parameter | Value |
|---|---|
| Transport protocol | UDP |
| Pixel format | BGR565 (16-bit color) |
| Recommended resolution | 480×320 px |
| Supported displays | ST7796, ILI9341 (and compatible) |
| Server platform | .NET 8, Linux ARM64 / x64 |
| Client platform | ESP32 (ESP-IDF) |
| UDP Server port | 11000 |
| Grabber API port | 80 |

---

## Limitations & things to know

- **JavaScript is fully supported** — the page is rendered by a real browser (Chromium)
- **Page size** must match the display resolution
- **Refresh rate** is controlled by the server configuration
- **Network** — ESP32 and server must be on the same network, or the server must be publicly accessible
- **Colors** may look slightly different from a monitor due to the BGR565 color depth of TFT displays

---

## Part of OWLOS

ViewOwl is the latest project in the **OWLOS** open-source IoT line, developed with its maker community — not a one-off experiment.

- **[OWLOS](https://github.com/KirinDenis/owlos)** — the parent project: open-source IoT firmware & toolset for ESP32 / ESP8266 (47 stars, 14 forks)
- **[OWLOSAirQuality](https://github.com/KirinDenis/OWLOSAirQuality)** — air-quality monitoring built on OWLOS
- **Community** — [OWLOS group on Facebook](https://www.facebook.com/groups/OWLOS) (360+ members)

ViewOwl carries those lessons forward into a server-rendered display platform.

---

## Contributing

Templates, display drivers, bug fixes — all contributions are welcome!

1. Fork the repository
2. Create a branch: `git checkout -b feature/my-template`
3. Commit: `git commit -m 'Add: my weather template'`
4. Push: `git push origin feature/my-template`
5. Open a Pull Request

---

## License

MIT — see [License.md](License.md)

---

<div align="center">
  <sub>Made with by humans and AI · ViewOwl — your data deserves a beautiful screen</sub>
</div>
