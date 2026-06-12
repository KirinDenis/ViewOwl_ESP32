# Hardware Guide

This page covers supported display modules, wiring, and the hardware constraints that shaped ViewOwl's architecture — packet sizes, memory layout, SPI speed, and why things are the way they are.

---

## Supported displays

ViewOwl ships two firmware variants, one per display controller family.

### 480×320 — ST7796

| Property | Value |
|---|---|
| Resolution | 480 × 320 px |
| Color depth | 16-bit BGR565 |
| Interface | SPI |
| Frame size | **307,200 bytes** (480 × 320 × 2) |

Tested modules:

| Module | Notes | Link |
|---|---|---|
| Waveshare 3.5" SPI LCD (B) | ST7796S, no touch required | [Waveshare Wiki](https://www.waveshare.com/wiki/3.5inch_RPi_LCD_(B)) |
| Generic 3.5" ST7796 IPS | Common on AliExpress; verify chip before ordering | — |

### 320×240 — ILI9341

| Property | Value |
|---|---|
| Resolution | 320 × 240 px |
| Color depth | 16-bit BGR565 |
| Interface | SPI |
| Frame size | **153,600 bytes** (320 × 240 × 2) |

Tested modules:

| Module | Notes | Link |
|---|---|---|
| Waveshare 2.8" SPI LCD | ILI9341, very common, good quality | [Waveshare Wiki](https://www.waveshare.com/wiki/2.8inch_SPI_Module_ILI9341) |
| Adafruit 2.8" TFT | ILI9341, works out of the box | [Adafruit product page](https://www.adafruit.com/product/1651) |
| Generic 2.4" / 2.8" ILI9341 | Cheap and plentiful; check the controller chip label | — |

> **Tip:** When buying generic modules, look for "ILI9341" or "ST7796" printed on the driver IC on the back of the board. Some sellers label the same product with different controller names — the chip marking is the ground truth.

---

## Wiring

All SPI pins are the same for both display variants. Only the backlight pin differs.

| Display signal | ESP32 GPIO | Both variants |
|---|---|---|
| SCLK | **14** | ✓ |
| MOSI (SDA) | **13** | ✓ |
| MISO | **12** | ✓ |
| DC (RS) | **2** | ✓ |
| CS | **15** | ✓ |
| RST | 3.3 V (tie high) | ✓ |
| BL (backlight) | **27** — 480×320 only | |
| BL (backlight) | **21** — 320×240 only | |
| VCC | 3.3 V | ✓ |
| GND | GND | ✓ |

**RST:** The firmware uses a soft reset command at startup. Tie the RST pin to 3.3 V — no GPIO needed.

**Backlight:** The backlight is driven directly by a GPIO. If your module has a built-in current-limiting resistor on BL, connect it to the GPIO above. If the module has no BL pin and the backlight is always on, leave the GPIO unconnected.

---

## SPI bus speed

The SPI clock runs at **50 MHz**. At 50 MHz, a full 480×320 BGR565 frame (307,200 bytes) transfers to the display in approximately **49 ms** — fast enough for smooth rendering.

In practice the bottleneck is not SPI but the WiFi transfer from server to device (see [UDP packet sizing](#udp-packet-sizing) below). The display is always ready before the next frame arrives.

---

## ESP32 memory layout

Understanding the memory map explains several design decisions.

### RAM

The ESP32-WROOM-32 has approximately **320 KB of usable DRAM** for the application. A single 480×320 BGR565 frame is 307,200 bytes — almost the entire RAM budget. A 320×240 frame is 153,600 bytes.

ViewOwl solves this with a **streaming render buffer**: instead of holding the full frame in RAM, the firmware allocates a strip buffer covering 80 horizontal lines:

```
Buffer size = width × 80 lines × 2 bytes
  480×320:  480 × 80 × 2 = 76,800 bytes
  320×240:  320 × 80 × 2 = 51,200 bytes
```

The frame arrives over UDP in chunks, accumulates in the strip buffer, and is flushed to the display via SPI as each strip fills. The device never holds more than one strip in RAM at a time.

### Flash partition — large frame storage

For Class C animated templates, all frames of the animation are stored in a dedicated **`frames` flash partition**. This partition holds up to several hundred kilobytes of pre-rendered BGR565 data and is memory-mapped for fast sequential reads.

The firmware selects flash storage automatically when the incoming frame batch exceeds **90 KB** (the `FLASH_THRESHOLD`). Below that threshold, a single frame fits in DRAM and is rendered directly. Above it, the batch is written to the flash partition and played back from there.

---

## UDP packet sizing

ViewOwl uses UDP — not TCP — for frame delivery. UDP has no built-in retransmission, which means the application implements its own ACK protocol. The packet size of **1,024 bytes** was chosen to balance:

- **WiFi MTU:** The practical payload limit for UDP over 802.11 (after IP + UDP headers) is around 1,400 bytes. 1,024 bytes comfortably fits within this without fragmentation.
- **ACK overhead:** Smaller packets mean more round-trips per frame. Larger packets increase the cost of a retransmit. 1,024 bytes is a well-established sweet spot for this kind of protocol.
- **Latency:** At 1,024 bytes per packet, a 307,200-byte frame requires 300 packets. On a good WiFi link this takes 1–2 seconds. On a congested network the firmware retries individual packets, not the whole frame.

Each packet carries a 15-byte header (magic number, type, session ID, sequence, payload length, flags) leaving **1,009 bytes** of payload per packet.

---

## Frame format — BGR565

All frames are raw **BGR565** (16 bits per pixel, little-endian). This matches the native format expected by ILI9341 and ST7796 controllers, so the firmware sends pixel data directly to the display over SPI with no conversion step.

2 bytes per pixel × 480 × 320 = **307,200 bytes** per frame.  
2 bytes per pixel × 320 × 240 = **153,600 bytes** per frame.

The server also supports compressed frame delivery (palette+RLE and palette+LZ4) for Class C templates. The firmware detects the format from a flag byte at the start of the payload and decompresses on the fly.

---

## Power

The ESP32 and display draw from 3.3 V. Typical current consumption:

| State | Current (approx.) |
|---|---|
| WiFi connected, idle | 80–120 mA |
| Receiving frame over UDP | 150–200 mA |
| Display rendering (SPI active) | +30–50 mA |

A standard USB power bank or 5 V 1 A adapter is sufficient. Use a 3.3 V LDO regulator if powering from a bare LiPo cell.

---

## Choosing between 480×320 and 320×240

| | 480×320 | 320×240 |
|---|---|---|
| Frame size | 307 KB | 150 KB |
| Transfer time (good WiFi) | ~2 s | ~1 s |
| Display cost | slightly higher | very cheap |
| Template variety | full library | full library |
| Best for | wall panels, desk displays | small badges, embedded |

Both sizes have access to the full template library. Templates are rendered server-side at the exact resolution of your display, so you always get a pixel-perfect fit.
