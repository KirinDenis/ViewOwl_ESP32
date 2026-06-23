# ViewOwl.ESP32_C3.Client

## What it does

ViewOwl firmware for **round GC9A01 displays on ESP32-C3** boards (Elecrow CrowPanel
1.28"). A separate client from `../ViewOwl.ESP32.Client` (which targets classic ESP32 +
ST7796/ILI9341/ILI9486) — kept apart so the working firmware is never at risk and the
C3-specific code stays free of `#ifdef` sprawl.

The **wire protocol** (packet format, CRC, the HELLO/AUTH/DATA/ACK/DONE state machine)
is identical to the classic client and the C# server — that contract must never drift.
SoC-agnostic logic (UDP, frame decode, WiFi, NVS) is shared from the classic client where
possible; only the LCD layer + board config are C3-specific.

## Requirements

- ESP-IDF **5.5**
- Elecrow CrowPanel ESP32-C3 1.28" round display (GC9A01, 240×240, PI4IOE5V6408 expander)

## Build & flash

```bash
# from this directory, ESP-IDF 5.5 activated:
idf.py set-target esp32c3      # first time (the VS Code extension's target also wins —
                               # set it to esp32c3 in the status bar)
idf.py build flash monitor
```

## Milestones

| | |
|---|---|
| **M1** ✅ | project builds, boots on C3, draws a boot screen |
| **M2** ✅ | WiFi station + serial provisioning (SSID/pass/token to NVS) |
| **M3** ✅ | UDP client (shared `packet.h` + `udp_*`/`frame_*` logic) → receive + render a 240×240 frame |
| **M4** ✅ | Class C: BATCH → `frames` flash partition → offline playback at the batch fps |
| M5 | board config + token finalised; fold shared logic into a common component |

All of M1–M4 is proven on hardware: the round display authenticates against the
production server, receives a Class-C batch, stores it in the `frames` partition, and
plays the animation from flash — independent of the link, so a server hiccup never
stalls playback.

### Class-C playback (M4)

The whole batch (~516 KB for 16 frames) does not fit in C3 RAM, so frames stream to a
dedicated `frames` flash partition (see `partitions.csv`) and the player reads one frame
at a time. `nvs` is kept at its default offset/size, so `idf.py flash` preserves the
Wi-Fi creds + token — no re-provision after a reflash (only `erase-flash` wipes NVS).

### Resilience

The HELLO loop self-heals a wedged socket: after `HELLO_FAIL_MAX` consecutive replies-
less HELLOs the socket is closed and recreated, so an unreachable server at boot no
longer leaves the client stuck until a manual reboot.

## Hardware notes

See the memory skill `skill_gc9a01_crowpanel` for the full pin map, the PI4IOE5V6408
expander register sequence, and the GC9A01 init — all proven on hardware (first flash).
The driver lives in `main/lcd_gc9a01.c`.
