#pragma once

/*
 * ViewOwl ESP32-C3 client - board/build config for the round GC9A01 display
 * (Elecrow CrowPanel 1.28"). Single board, so no multi-variant #ifdef like the
 * classic-ESP32 client. Protocol constants are mirrored from
 * ../../ViewOwl.ESP32.Client/main/config.h and MUST stay in sync.
 */

#define STRINGIFY(x) TOSTRING(x)
#define TOSTRING(x)  #x

/* Firmware version - overridden at build time by CI; fallbacks for local builds. */
#ifndef FIRMWARE_VERSION_MAJOR
#define FIRMWARE_VERSION_MAJOR 0
#endif
#ifndef FIRMWARE_VERSION_MINOR
#define FIRMWARE_VERSION_MINOR 1
#endif
#ifndef FIRMWARE_VERSION_PATCH
#define FIRMWARE_VERSION_PATCH 4
#endif
#ifndef FIRMWARE_BUILD_NUMBER
#define FIRMWARE_BUILD_NUMBER 0
#endif

#define FIRMWARE_VERSION_NUMVER \
    STRINGIFY(FIRMWARE_VERSION_MAJOR) "." \
    STRINGIFY(FIRMWARE_VERSION_MINOR) "." \
    STRINGIFY(FIRMWARE_VERSION_PATCH)
#define FIRMWARE_VERSION "OWLVIEW-C3-" FIRMWARE_VERSION_NUMVER "-b" STRINGIFY(FIRMWARE_BUILD_NUMBER)

/* Class-C playback pacing (mirrors the classic client):
 *   1 = FREE-RUN — render as fast as the decode + SPI path allows (IGNORES fps).
 *   0 = PACED — honour the template's encoded fps (render-time compensated).
 * PACED by default: the template author owns the rate — a 1-fps safety monitor must
 * not fly by. Motion templates that want max speed simply declare a high fps. */
#define PLAYER_FREE_RUN 0

/* -- Protocol timing (mirror of the classic client - keep in sync) ----------- */
#define PING_INTERVAL_S       30
#define BATCH_IDLE_S          300
#define IDLE_DURATION_S       1
#define BATCH_RETRY_DELAY_S   30
#define ACK_RETRY_MAX         3
#define HELLO_FAIL_MAX        15
#define FLASH_THRESHOLD       (90u * 1024u)

/* -- Network (placeholders - provisioned at runtime / via serial) ------------ */
#define WIFI_SSID "YOUR_WIFI_SSID"
#define WIFI_PASS "YOUR_WIFI_PASSWORD"
#define WIFI_MAXIMUM_RETRY 5
#define SERVER_IP "YOUR_SERVER_IP"
#define SERVER_PORT 11000

#define FRAMES_PARTITION_LABEL "frames"

/* -- Display: GC9A01 240x240 round (CrowPanel 1.28") ------------------------- */
#define LCD_H_RES 240
#define LCD_V_RES 240

/* DisplayType id for the server - GC9A01 round = 5 (ILI9341/ST7796=3, ILI9486=4). */
#define DISPLAY_TYPE_ID 5

/* Per-device token (demo placeholder GUID - replace per provisioned unit). */
#define TOKEN "b7d3f1a0-2c4e-4a6b-8c1d-e0f3a5b7c9d1"
/*
 * TOKEN_BYTES - binary Windows GUID for the TOKEN above.
 * Layout: Data1(LE) Data2(LE) Data3(LE) Data4(BE)
 *   B7D3F1A0 -> A0 F1 D3 B7
 *   2C4E     -> 4E 2C
 *   4A6B     -> 6B 4A
 *   8C1DE0F3A5B7C9D1 -> 8C 1D E0 F3 A5 B7 C9 D1
 */
#define TOKEN_BYTES { \
    0xA0, 0xF1, 0xD3, 0xB7, 0x4E, 0x2C, 0x6B, 0x4A, \
    0x8C, 0x1D, 0xE0, 0xF3, 0xA5, 0xB7, 0xC9, 0xD1  \
}

#define LCD_H_LINES 40
#define LCD_BUFFER_SIZE (LCD_H_RES * LCD_H_LINES * sizeof(uint16_t))

/* Raw frame size: 240 x 240 x 2 bytes (BGR565) = 115 200 bytes. */
#define IMAGE_SIZE ((uint32_t)(LCD_H_RES) * (uint32_t)(LCD_V_RES) * sizeof(uint16_t))
