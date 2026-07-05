# diagnostics-wide — WIDE 792×272 e-paper bring-up polygon

## What it does

A standalone ESP-IDF "polygon" (no ViewOwl stack) for bringing up the **Elecrow CrowPanel
5.79" e-paper** (792×272, black/white). The panel is **"made of two"** 396×272 halves
driven by **two SSD1683 controllers in a master/slave cascade** — same SSD1683 family and
same wiring as our 4.2", but the second half is the new part.

This project drives the **full panel** (both halves, one coherent image), then the working
driver gets folded into `ViewOwl.ESP32_EPD.Client` + the display-type registry.

## The key facts (from the Elecrow vendor reference)

Vendor repo: `github.com/Elecrow-RD/CrowPanel-ESP32-5.79-E-paper-HMI-Display-with-272-792`
(demo `5.79_WIFI_refresh`). The driver here is a **clean ESP-IDF rewrite**, used only to
learn the cascade sequence — not copied (the vendor demo also carries an OpenWeatherMap key
and is Arduino-only).

- **ONE chip-select, ONE SPI bus.** There is *no* second CS. The two controllers are
  addressed by **different command opcodes**:
  | | window X/Y | cursor X/Y | RAM | enable |
  |---|---|---|---|---|
  | master half | 0x44 / 0x45 | 0x4E / 0x4F | 0x24 / 0x26 | 0x11 (data entry) |
  | slave half | 0xC4 / 0xC5 | 0xCE / 0xCF | 0xA4 / 0xA6 | 0x91 (its data entry: 0x04 mono, 0x00 gray) |

  (slave opcode = master opcode with bit7 set). A single `0x22`/`0x20` activation refreshes
  **both** halves at once.
- **Geometry:** each SSD1683 is 400 wide but only **396 visible** → an **8-column gap** at
  the seam. RAM buffer = **800×272** (100 bytes/row, 27200 B); visible image = **792×272**;
  logical pixels at `x >= 396` are shifted **+8** to skip the gap. Default orientation is
  **Rotation 180** (X and Y mirrored); the slave half is additionally **X-mirrored** in RAM
  to align across the seam. This seam/mirror alignment was Denys's original blocker.

## Wiring (shared, same as the 4.2")

| Line | GPIO |
|---|---|
| SCK / MOSI | 12 / 11 |
| RES / DC / CS / BUSY | 47 / 46 / 45 / 48 |
| PWR (power-enable, must be HIGH) | 7 |

## Requirements

- ESP-IDF 5.5, target **esp32s3** (GPIO 45–48 are S3-only)
- The Elecrow CrowPanel 5.79" panel wired as above

## Build & flash (user flashes locally to iterate fast)

```sh
cd ViewOwl.ESP32_EPD.Client/diagnostics-wide
idf.py set-target esp32s3      # first time, or if a stale sdkconfig exists
idf.py build flash monitor
```

## Milestone 1 — mono bring-up (DONE, first flash)

The original probe painted one image spanning both halves: full-perimeter border, a big
corner-to-corner "X", a seam line at x=396, an asymmetric block, and half-id ticks. It
confirmed on the first flash that the cascade driver + gap/mirror mapping are correct: the
border closed, the X crossed the seam unbroken, no mirror. The mono driver then shipped in
`ViewOwl.ESP32_EPD.Client` as the `EPD_792x272` build variant.

## Milestone 2 — 4-gray (DONE, four probes; `main.c` is probe v4)

The panel has no vendor grayscale mode, so 4-gray was won experimentally. The probe history
is worth keeping — it documents how this cascade actually behaves:

- **v1 — 4.2" LUT transplanted, our column-major geometry.** Result: the master half
  rendered 4 clean tones (the glass CAN do gray!), the slave half was dotted mud, and only
  the top half of the panel updated. Also proved the slave-LUT question moot: plain LUT
  opcodes appear to reach both controllers.
- **v2 — write-order and activation experiments.** Row-band pattern measured the vertical
  coverage: exactly gates 0..135 — half the panel. `0xC7` activation contaminated the next
  screen (e-paper state carries over between attempts).
- **v3 — explicit MUX (0x01) writes.** Made it *worse* (image compressed into a quarter):
  the vendor never writes 0x01 on this cascade — reverted. Plane-isolation screens narrowed
  the slave fault to its second (0xA6) stream.
- **v4 — verbatim Waveshare port (CURRENT `main.c`).** Waveshare sells the same glass and
  ships an open 4-gray driver (`waveshareteam/e-Paper`, `EPD_5in79.c`). Ported command for
  command: their LUT, booster tail `0xA6`, border `0x3C=0x81`, **row-major** data entry
  (`0x11=0x01` master / `0x91=0x00` slave — 0x91 turned out to be the slave's data-entry
  register, the mirrored twin of 0x11, not an "enable"), both windows programmed once, four
  full-plane streams with **no cursor rewrites**, activation `0x22=0xCF`. The image is a
  plain linear 2bpp buffer — the hardware does the mirroring, and the seam hides 4+4
  columns (master reads row bytes 0..99, slave 98..197). **Confirmed on hardware: both
  halves, full height, four clean tones.** The panel renders the logical frame rotated
  180°, so the production driver packs with both coordinates flipped.

## Status

Both milestones are folded into `ViewOwl.ESP32_EPD.Client` (`epd_wide.c`, v0.3.0):
mono 1-bit and 4-gray full refresh. Still open: true partial refresh on the cascade
(`0x22=0xDC`) and Class-C playback — this polygon is the place to prototype them.
