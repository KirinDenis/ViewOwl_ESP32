#pragma once

/* Firmware build trigger: 2026-06-26a — v1.2.50 BATCH_MAX_FRAMES 16→32 (packet.h, matches server BatchStartPayload.MaxFrames): allows Class-C loops up to 32 frames (e.g. the 30-frame trench). Wire-compatible with ≤16-frame batches; this build is required for >16-frame templates. */
/* STRINGIFY / TOSTRING — needed to expand FIRMWARE_BUILD_NUMBER to a string literal */
#define STRINGIFY(x) TOSTRING(x)
#define TOSTRING(x)  #x

/* Firmware version — overridden at build time by CI via -DFIRMWARE_VERSION_MAJOR=x etc.
 * Fallback values are used for local builds without git tags. */
#ifndef FIRMWARE_VERSION_MAJOR
#define FIRMWARE_VERSION_MAJOR 1
#endif
#ifndef FIRMWARE_VERSION_MINOR
#define FIRMWARE_VERSION_MINOR 2
#endif
#ifndef FIRMWARE_VERSION_PATCH
#define FIRMWARE_VERSION_PATCH 50
#endif
#ifndef FIRMWARE_BUILD_NUMBER
#define FIRMWARE_BUILD_NUMBER 0
#endif

#define FIRMWARE_VERSION_NUMVER \
    STRINGIFY(FIRMWARE_VERSION_MAJOR) "." \
    STRINGIFY(FIRMWARE_VERSION_MINOR) "." \
    STRINGIFY(FIRMWARE_VERSION_PATCH)
#define FIRMWARE_VERSION "OWLVIEW-" FIRMWARE_VERSION_NUMVER "-b" STRINGIFY(FIRMWARE_BUILD_NUMBER)

/* Heartbeat interval — how often the device sends PACKET_PING while idle */
#define PING_INTERVAL_S 30

/* After a successful BATCH_COMMIT the player runs independently from flash.
 * Use a long idle so the player completes many animation cycles before the
 * device sends a new HELLO.  The server breaks idle early via AUTH trigger
 * when new content is available, so this is only a safety-net fallback. */
#define BATCH_IDLE_S 300  /* 5 minutes */

/* Idle window duration — how long the device waits before fetching a new frame */
#define IDLE_DURATION_S 1

/* Class-C playback pacing mode:
 *   1 = FREE-RUN — ignore the template's encoded fps and play as fast as the
 *       decode + SPI path allows (render-bound).  Use while templates still
 *       carry a low data-vow-fps and you want the hardware's real speed now.
 *   0 = PACED — honour the encoded fps as a ceiling (render-time compensated).
 *       Switch to this once templates carry the desired data-vow-fps (e.g. 15). */
#define PLAYER_FREE_RUN 0

/* Back-off delay after a failed BATCH transfer (timeout, sequence error, etc.).
 * Prevents a tight 1-second retry loop when the server is temporarily overloaded.
 * The early-AUTH push from the server breaks this idle immediately when a fresh
 * batch is ready, so this value is a ceiling, not a floor. */
#define BATCH_RETRY_DELAY_S 30

#define WIFI_SSID "YOUR_WIFI_SSID"
#define WIFI_PASS "YOUR_WIFI_PASSWORD"
#define WIFI_MAXIMUM_RETRY 5

#define SERVER_IP "YOUR_SERVER_IP"
#define SERVER_PORT 11000

#define FRAMES_PARTITION_LABEL "frames"

/* Flash receive threshold: DATA chunks are written to the image partition when
 * the server reports a total frame size above this value in the AUTH Seq field.
 * Keeps large frames (e.g. photographic / raw fallback) out of the 106 KB DRAM
 * receive buffer; rendered via memory-mapped read after DONE.
 * See: docs/hardware.md — "Flash partition / large frame storage" */
#define FLASH_THRESHOLD (90u * 1024u)

/* Maximum number of times the last ACK is resent while waiting for the next
 * DATA packet or for DONE after FLAG_LAST.  Each attempt waits one full
 * SO_RCVTIMEO period (3 s).  Maximum stall before aborting: 3 × 3 s = 9 s. */
#define ACK_RETRY_MAX 3

/* Consecutive HELLO non-responses before the UDP socket is closed and
 * recreated.  At ~5 s per attempt this allows ~75 s of outage before reset. */
#define HELLO_FAIL_MAX 15

/* Display variant is selected at build time via cmake flags:
 *   -DLCD_ILI9486=1  → _ILI9486_LCD  (ILI9486 480×320, RPi-compatible)
 *   -DLCD_480x320=1  → _480x320_LCD  (ST7796  480×320)
 *   (no flag)        →               (ILI9341 320×240)
 * Do NOT define these here — cmake controls which binary is built. */

/* Display resolution, backlight pin, token, and display type ID —
 * one variant is active per build.
 * See: docs/hardware.md — "Supported displays" and "Wiring" */
#ifdef _ILI9486_LCD
#define LCD_H_RES 480
#define LCD_V_RES 320
/* Backlight wired permanently to power on RPi-compatible boards — no GPIO control. */
#define LIGHT_PIN -1
#define TOKEN "e8f9a0b1-c2d3-4e5f-a6b7-c8d9e0f1a2b3"
/* DisplayType: ILI9486 = 4 */
#define DISPLAY_TYPE_ID 4
/*
 * TOKEN_BYTES — binary Windows GUID for "e8f9a0b1-c2d3-4e5f-a6b7-c8d9e0f1a2b3".
 * Layout: Data1(LE) Data2(LE) Data3(LE) Data4(BE)
 *   E8F9A0B1 -> B1 A0 F9 E8
 *   C2D3     -> D3 C2
 *   4E5F     -> 5F 4E
 *   A6B7C8D9E0F1A2B3 -> A6 B7 C8 D9 E0 F1 A2 B3
 */
#define TOKEN_BYTES { \
    0xB1, 0xA0, 0xF9, 0xE8, 0xD3, 0xC2, 0x5F, 0x4E, \
    0xA6, 0xB7, 0xC8, 0xD9, 0xE0, 0xF1, 0xA2, 0xB3  \
}
#elif defined(_480x320_LCD)
#define LCD_H_RES 480
#define LCD_V_RES 320
#define LIGHT_PIN 27
#define TOKEN "b2c3d4e5-f6a7-4b8c-9d0e-1f2a3b4c5d6e"
/* DisplayType: ST7796 = 3 */
#define DISPLAY_TYPE_ID 3
/*
 * TOKEN_BYTES — binary Windows GUID for "b2c3d4e5-f6a7-4b8c-9d0e-1f2a3b4c5d6e".
 * Layout: Data1(LE) Data2(LE) Data3(LE) Data4(BE)
 *   B2C3D4E5 -> E5 D4 C3 B2
 *   F6A7     -> A7 F6
 *   4B8C     -> 8C 4B
 *   9D0E1F2A3B4C5D6E -> 9D 0E 1F 2A 3B 4C 5D 6E
 */
#define TOKEN_BYTES { \
    0xE5, 0xD4, 0xC3, 0xB2, 0xA7, 0xF6, 0x8C, 0x4B, \
    0x9D, 0x0E, 0x1F, 0x2A, 0x3B, 0x4C, 0x5D, 0x6E  \
}
#else
#define LCD_H_RES 320
#define LCD_V_RES 240
#define LIGHT_PIN 21
#define TOKEN "a47cd5fa-626d-4c5b-9fcd-b13b2bdc2e32"
/* DisplayType: ILI9341 = 3 */
#define DISPLAY_TYPE_ID 3
/*
 * TOKEN_BYTES — binary Windows GUID for "a47cd5fa-626d-4c5b-9fcd-b13b2bdc2e32".
 * Layout: Data1(LE) Data2(LE) Data3(LE) Data4(BE)
 *   A47CD5FA -> FA D5 7C A4
 *   626D     -> 6D 62
 *   4C5B     -> 5B 4C
 *   9FCDB13B2BDC2E32 -> 9F CD B1 3B 2B DC 2E 32
 */
#define TOKEN_BYTES { \
    0xFA, 0xD5, 0x7C, 0xA4, 0x6D, 0x62, 0x5B, 0x4C, \
    0x9F, 0xCD, 0xB1, 0x3B, 0x2B, 0xDC, 0x2E, 0x32  \
}
#endif

/* Strip height: halved from 80 to 40 so TWO ping-pong DMA buffers
 * (2 × LCD_BUFFER_SIZE) fit the same SRAM budget the old single 80-line buffer
 * used.  LCD_V_RES divides evenly by 40 for every variant (320/40=8, 240/40=6). */
#define LCD_H_LINES 40
#define LCD_BUFFER_SIZE (LCD_H_RES * LCD_H_LINES * sizeof(uint16_t))

/* Total raw frame size in bytes: width × height × 2 bytes per BGR565 pixel.
 * See: docs/hardware.md — "Frame format / BGR565" */
#define IMAGE_SIZE ((uint32_t)(LCD_H_RES) * (uint32_t)(LCD_V_RES) * sizeof(uint16_t))
