# Wide dual-half e-paper — new series KICKOFF

> Task brief for the new session. Same SSD1683 family as the 4.2" (400×300), but this panel is
> **"made of two"** — two halves / two controllers. Our 4.2" code lit a picture on **ONE half**.
> Goal: drive the **full panel** (both halves), then fold into `ViewOwl.ESP32_EPD.Client` + registry.
> This is an **extension** of the existing EPD client (same controller, pins, stack) — not from scratch.

## Grounded facts (from the test code)
- **Controller: SSD1683**, 1-bit. Command set (identical to our 4.2"): `0x12` soft-reset,
  `0x21`/`0x3C`/`0x11`, `0x44`/`0x45` RAM X/Y window, `0x4E`/`0x4F` cursor, `0x22`(+`0xF7` full /
  `0xFF`/`0xC7` modes)/`0x20` activate. Half = **400×300**, framebuffer 1-bit 15000 B.
- **Pins (from the test):** `SCK 12  MOSI 11  DC 46  CS 45  BUSY 48` (+ `PWR 7`). **One CS = one half.**
- **Reference test (proven, single-half):** branch `Denys/Phase_Three` →
  `Phase Three/4.2_partial_refresh_Text/` (`EPD.cpp/.h`, `EPD_SPI.cpp/.h`, `EPD_GUI`, `.ino`).
- **Clean working driver to build on:** `../main/epd_4in2.c` and the existing `../diagnostics/` polygon.
- ⚠️ **The reference .ino has a leaked OpenWeatherMap `apiKey` (line 13) — DO NOT carry it.**
  Use Open-Meteo (we already have the Trenčín weather template). OpenWeatherMap is prohibited.

## How we work (step-by-step, like the C3/EPD bring-up)
- This folder is the **polygon**: a standalone, clean ESP-IDF project, **no ViewOwl stack yet** —
  just light up the panel and experiment.
- **Flash loop:** the USER flashes locally via `idf.py` to iterate fast. Claude only edits; never
  builds firmware locally. Once the dual-half driver works → fold into the CI-built client (push dev → CI).

## Steps
0. **Inspect hardware (fastest answer):** count FPC ribbons + CS lines + BUSY lines; look for an
   M/S (master/slave) marking; read the model number off the FPC; measure full panel WxH.
   The markings/datasheet often answer the whole thing.
1. **Baseline:** port the clean SSD1683 driver here, build, reproduce the KNOWN-GOOD — a picture on
   ONE half (CS45). This is the anchor.
2. **Find the 2nd half** (write a distinct pattern per half, watch which physical region updates).
   Test hypotheses in order:
   - **(a) Two CS** (most likely for "made of two"): add `CS2`, wire to the panel's 2nd CS, init +
     write the 2nd controller identically. Check if BUSY is shared or per-half.
   - **(b) Master/slave, one CS**: one controller addresses both via master/slave commands / a 2nd
     RAM region on the shared bus.
   - **(c) One bigger controller** (e.g. 648×480): set EPD_W/H to full size — maybe 400×300 just
     filled a sub-window, not a "second half".
3. **Drive the FULL panel:** split the frame into the two halves, write each, wait BOTH BUSY,
   refresh. Verify one image spans both halves — aligned, no gap/overlap/mirror (fix MADCTL /
   data-entry / cursor origin per half).
4. **Partial-refresh + Class-C:** replicate the EPD fast playback (partial waveform `0x22=0xFF`,
   no periodic full clear) across both halves.
5. **Fold polygon → product:** likely a **build-flag variant** of the EPD client (like `LCD_480x320`
   for classic) selecting the dual-half driver + full EPD_W/H, OR a new client if it diverges.
   Reuse `provision.c` (USB-JTAG + Web Serial terminal), `udp_client`, player, `packet.h`. Add the
   registry entry (`display-types.json` family epd, displayType `epd-<WxH>`, frameFormat `mono1bit`),
   `DisplayTypeCatalog` + `DisplayType` enum, manifest, BurnModal entry, CI build step (non-fatal,
   S3 bootloader @0x0). Version per `config.h`.
6. **Server/template:** grabber already renders at the device W/H; the mono converter handles the
   full-panel 1-bit. Re-lay-out the Trenčín weather template for the wide aspect (black-on-white).

## Open questions (resolve in steps 0–2)
Number of CS / BUSY lines; M/S marking; full resolution + half orientation (side-by-side 800×300 vs
stacked 400×600 vs one bigger 648×480); shared vs per-half BUSY; alignment/mirror between halves; model.

_Full background also in Claude memory: `project_epaper_dual_half_kickoff` (+ `skill_epd_4in2_ssd1680`,
`skill_add_display_type`)._
