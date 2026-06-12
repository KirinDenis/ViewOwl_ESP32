# ViewOwl

**ViewOwl puts live web pages on small dedicated screens.** Write an ordinary HTML page and it appears on a little display on your wall: a weather widget, a status board, a café menu, an animated sci-fi panel. If you can make a web page, you can make a device.

The screens hold no logic — a small self-hosted server renders every page and sends ready-made pictures to each screen over Wi-Fi. Animated templates are stored inside the device and keep looping even with no network at all.

The whole system stays light: the server runs comfortably on a small VPS or a single-board computer, and devices are flashed and configured straight from the browser — no drivers, no IDE, no toolchain.

---

## How it works

```
HTML template
      │
      ▼
Headless Chromium (ARM64)
      │  PNG → resize → BGR565
      ▼
ExchangeFolder/{guid}.bin
      │
      ▼
UDP Server ──────────────────► ESP32 + SPI LCD
  custom ACK protocol             renders frame
  1400-byte packets
```

---

## Template classes

| Class | Grab method | Animation | Use case |
|---|---|---|---|
| **A** | Server-side Chromium | No | Clocks, weather, live data |
| **B** | Server grab or browser push | No | Canvas widgets, live feed |
| **C** | Server-side batch | Yes (flash, offline) | Animated displays, sci-fi effects |

---

## Quick links

- **New here?** → [Getting Started](getting-started.md)
- **Wiring your display** → [Hardware](hardware.md)
- **Writing templates** → [Template Overview](templates/overview.md)
- **Self-hosting on a VPS** → [Self-Hosting](server/self-hosting.md)
- **UDP protocol details** → [UDP Transport Reference](reference/udp-transport.md)
- **Full REST API** → [API Reference](reference/api-reference.md)
