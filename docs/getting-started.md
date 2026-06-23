# Getting Started with ViewOwl

ViewOwl turns an ESP32 and a cheap SPI display into a live information panel — weather, flight tracker, stock ticker, custom animations — updated automatically over WiFi. No code required on the device side.

This guide takes you from unboxing to a working display in about 10 minutes.

---

## What you need

### Hardware

| Part | Notes |
|---|---|
| **ESP32 dev board** | ESP32-WROOM-32 or any standard 38-pin ESP32 module |
| **SPI LCD display** | 480×320 (ST7796) or 320×240 (ILI9341) — see [Hardware guide](hardware.md) for tested modules. A 240×240 **round** GC9A01 panel (integrated ESP32-C3) is also supported — flashed the same way. |
| **USB cable** | Data cable, not charge-only |
| **Computer** | Windows, macOS, or Linux with Chrome or Edge |

> **Display note:** The two firmware variants are built for specific controllers. Before buying, confirm the display chip on the product page. ILI9341 is the most common 320×240 chip; ST7796 is standard for 480×320 panels.

### Wiring

Connect your display to the ESP32 using SPI. The firmware uses these GPIO pins:

| Display pin | ESP32 GPIO | Notes |
|---|---|---|
| SCLK (CLK) | **14** | SPI clock |
| MOSI (SDA) | **13** | SPI data |
| MISO | **12** | Not used by most displays, connect if present |
| DC (RS) | **2** | Data / Command select |
| CS | **15** | Chip select |
| RST | 3.3 V | Tie high — firmware uses soft reset |
| BL (backlight) | **27** (480×320) / **21** (320×240) | Backlight enable |
| VCC | 3.3 V | |
| GND | GND | |

> If your display module has a built-in backlight resistor and no BL pin, you can leave that pin unconnected and the display will be permanently lit.

---

## Step 1 — Pick a template

Open the ViewOwl gallery at **[view.owlos.sk](https://view.owlos.sk)** in Chrome or Edge.

Browse the template gallery. Use the **Class** and **Category** filters on the left to narrow things down.

Click a template card to preview it. The preview shows the actual render at your display's resolution.

When you find one you like, select your display from the dropdown (**480×320**, **320×240**, or **240×240 round**) and click **▸ FLASH**.

> **Round display:** the 240×240 round panel flashes and provisions exactly like the rectangular boards. Its content is authored as round-native templates — see [Class C → round-native templates](templates/class-c.md) for the proving ground and geometry rules.

---

## Step 2 — Flash the firmware

The flash wizard runs entirely in your browser using the Web Serial API — no drivers, no IDE, no app to install.

1. Connect your ESP32 via USB.
2. Click **Connect device** — Chrome opens a port selector.
3. Select your ESP32 from the list (usually listed as "USB Serial" or "CP210x").
4. The wizard flashes the firmware automatically. It takes about 30 seconds.
5. A progress bar shows each stage: erase → write → verify.

> **If your board doesn't appear in the list:** Hold the BOOT button on the ESP32 while clicking Connect, then release it. Some boards require this to enter flash mode.

> **Linux only:** You may need to add yourself to the `dialout` group:
> ```bash
> sudo usermod -aG dialout $USER
> ```
> Log out and back in for the change to take effect.

---

## Step 3 — Connect to WiFi

After flashing, the firmware reboots and opens a serial terminal for WiFi provisioning.

The wizard shows a terminal window directly in your browser. Type your WiFi credentials when prompted:

```
SSID: YourNetworkName
Password: YourPassword
```

The device saves credentials to flash and reboots. The display shows a boot screen while it connects. Once connected, the template you selected starts rendering within a few seconds.

> Credentials are stored in the ESP32's NVS (non-volatile storage) and survive reboots and power cycles. You only need to provision once — unless you move the device to a different network.

---

## Step 4 — Watch it work

That's it. The display now shows your chosen template, refreshed automatically from the ViewOwl server.

Most templates update every few minutes. Animated templates (Class C) run a looping animation stored in the device's flash — these play continuously without any server connection.

---

## Changing the template later

You don't need to reflash to change templates. Open [view.owlos.sk](https://view.owlos.sk), pick a new template, and use the **PUSH** button in the preview panel. The new frame is sent to your device over the network within seconds.

For a permanent change — one that survives a reboot — use **FLASH** again. The new template is written to the device the same way as the first time.

---

## What each template class means

| Class | How it works | Refresh |
|---|---|---|
| **A** — Live data | Fetches a URL on the server, grabs a screenshot | Every 5 minutes |
| **B** — Server-rendered | HTML template rendered on the server, pushed to device | Configurable |
| **C** — Animated | Multi-frame animation stored in device flash | Plays locally, no server needed |

See [Templates overview](templates/overview.md) for full details and how to build your own.

---

## Self-hosting

The ViewOwl server is open source. If you want to run your own instance — for privacy, custom templates, or offline use — see the [Self-hosting guide](server/self-hosting.md).

Self-hosting requires a Linux server (ARM64 or x86-64), Docker, and a domain or local network. The setup takes about 20 minutes.

---

## Troubleshooting

**Display stays blank after flashing**
- Check wiring, especially DC (GPIO 2) and CS (GPIO 15).
- Confirm you flashed the correct firmware variant for your display size.
- The backlight pin must be connected and driven high — check BL wiring.

**Device connects to WiFi but nothing appears**
- The server may be unreachable from your network. Check that UDP port 11000 is not blocked by your router or firewall.
- Open the admin panel → Devices to confirm your device is listed and its status shows "Connected".

**"No ports available" in the flash wizard**
- Use Chrome or Edge — Firefox does not support Web Serial.
- On Linux, add your user to the `dialout` group (see Step 2 above).
- Try a different USB cable.

**Flash wizard fails midway**
- Hold BOOT on the ESP32 and click Connect again to force flash mode.
- If the error persists, try erasing the chip first using the "Erase" option in the wizard.

---

## Next steps

- [Hardware guide](hardware.md) — tested display modules, enclosure ideas, power options
- [Templates overview](templates/overview.md) — how templates work and how to make your own
- [Self-hosting](server/self-hosting.md) — run ViewOwl on your own server
- [Contributing](../CONTRIBUTING.md) — how to submit templates, bug reports, or code
