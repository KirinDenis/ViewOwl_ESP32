#pragma once

/*
 * ViewOwl ESP32-S3 e-paper client - board/build config for the 4.2" 400x300
 * SSD1683 panel. Single board, so no multi-variant #ifdef. Protocol constants are
 * mirrored from ../../ViewOwl.ESP32.Client/main/config.h and MUST stay in sync.
 */

#define STRINGIFY(x) TOSTRING(x)
#define TOSTRING(x)  #x

/* Firmware version - overridden at build time by CI; fallbacks for local builds. */
#ifndef FIRMWARE_VERSION_MAJOR
#define FIRMWARE_VERSION_MAJOR 0
#endif
#ifndef FIRMWARE_VERSION_MINOR
/* 0.3.0 (2026-07-04): 4-gray on the 792x272 cascade — verbatim Waveshare
 * EPD_5in79 port (register LUT, row-major dual-controller addressing, 0xCF),
 * verified on hardware by the diagnostics-wide probe v4. 4.2" path unchanged. */
#define FIRMWARE_VERSION_MINOR 3
#endif
#ifndef FIRMWARE_VERSION_PATCH
#define FIRMWARE_VERSION_PATCH 0
#endif
#ifndef FIRMWARE_BUILD_NUMBER
#define FIRMWARE_BUILD_NUMBER 0
#endif

#define FIRMWARE_VERSION_NUMVER \
    STRINGIFY(FIRMWARE_VERSION_MAJOR) "." \
    STRINGIFY(FIRMWARE_VERSION_MINOR) "." \
    STRINGIFY(FIRMWARE_VERSION_PATCH)
#define FIRMWARE_VERSION "OWLVIEW-EPD-" FIRMWARE_VERSION_NUMVER "-b" STRINGIFY(FIRMWARE_BUILD_NUMBER)

/* -- Protocol timing (mirror of the classic client - keep in sync) ----------- */
#define PING_INTERVAL_S       30
#define BATCH_IDLE_S          300
#define IDLE_DURATION_S       1
#define BATCH_RETRY_DELAY_S   30
#define ACK_RETRY_MAX         3
#define HELLO_FAIL_MAX        15

/* -- Network (placeholders - provisioned at runtime / via serial) ------------ */
#define WIFI_SSID "YOUR_WIFI_SSID"
#define WIFI_PASS "YOUR_WIFI_PASSWORD"
#define WIFI_MAXIMUM_RETRY 5
#define SERVER_IP "YOUR_SERVER_IP"
#define SERVER_PORT 11000

#define FRAMES_PARTITION_LABEL "frames"

/* -- Display ----------------------------------------------------------------
 * Two panels, selected by the EPD_792x272 build flag (-DEPD_792x272=1 from CI /
 * idf.py). Default = 4.2" 400x300 single SSD1683 (DisplayType 6). The wide variant =
 * 5.79" 792x272 SSD1683 master/slave cascade (DisplayType 7). DisplayType ids on the
 * server: ILI9341=2, ST7796=3, ILI9486=4, GC9A01=5, EPD=6, EPDWIDE=7. */
#if defined(EPD_792x272)
#define LCD_H_RES 792
#define LCD_V_RES 272
#define DISPLAY_TYPE_ID 7
#define PANEL_DESC "5.79\" 792x272 cascade"
#else
#define LCD_H_RES 400
#define LCD_V_RES 300
#define DISPLAY_TYPE_ID 6
#define PANEL_DESC "4.2\" 400x300 SSD1683"
#endif

/* Per-device token (demo placeholder GUID - replace per provisioned unit). */
#define TOKEN "c2e6a4d0-9b3f-4e1a-8d72-1f5b3c7e9a04"
/*
 * TOKEN_BYTES - binary Windows GUID for the TOKEN above.
 * Layout: Data1(LE) Data2(LE) Data3(LE) Data4(BE)
 *   C2E6A4D0 -> D0 A4 E6 C2
 *   9B3F     -> 3F 9B
 *   4E1A     -> 1A 4E
 *   8D721F5B3C7E9A04 -> 8D 72 1F 5B 3C 7E 9A 04
 */
#define TOKEN_BYTES { \
    0xD0, 0xA4, 0xE6, 0xC2, 0x3F, 0x9B, 0x1A, 0x4E, \
    0x8D, 0x72, 0x1F, 0x5B, 0x3C, 0x7E, 0x9A, 0x04  \
}
