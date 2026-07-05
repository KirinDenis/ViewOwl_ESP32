/**
 * @file epd_wide.h
 * @brief WIDE 792x272 e-paper driver — Elecrow CrowPanel 5.79" (TWO SSD1683 ICs,
 *        master/slave cascade on ONE SPI bus / ONE chip-select).
 *
 * Identified from the vendor reference (Elecrow CrowPanel-ESP32-5.79-E-paper, demo
 * 5.79_WIFI_refresh): the panel is "made of two" 396x272 halves driven by two cascaded
 * SSD1683 controllers that share SCK/MOSI/DC/CS/BUSY. There is NO second chip-select —
 * the two controllers are addressed by DIFFERENT command opcodes:
 *   - master half: 0x44/0x45 window, 0x4E/0x4F cursor, 0x24/0x26 RAM
 *   - slave  half: 0xC4/0xC5 window, 0xCE/0xCF cursor, 0xA4/0xA6 RAM
 *     (the master opcode with bit7 set), gated on by register 0x91 = 0x04.
 * A single 0x22/0x20 update activation refreshes both halves at once.
 *
 * Geometry (vendor): each SSD1683 is 400 wide but only 396 are visible; the two halves
 * leave an 8-column gap at the seam. So the RAM buffer is 800x272 (100 bytes/row), the
 * visible logical image is 792x272, and pixels at logical x>=396 are shifted +8 to skip
 * the gap. The default panel orientation is Rotation 180 (X and Y mirrored).
 *
 * This is a clean ESP-IDF rewrite (bit-banged SPI), not a copy of the Arduino driver —
 * it was used only to learn the cascade command sequence and the gap/mirror mapping.
 */
#pragma once

#include <stdint.h>

/* -- Geometry -------------------------------------------------------------- */
#define EPD_LOGICAL_W   792     /* visible image width  (drawable)            */
#define EPD_LOGICAL_H   272     /* visible image height (drawable)            */
#define EPD_MEM_W       800     /* RAM width incl. the 8px inter-panel gap     */
#define EPD_ROW_BYTES   (EPD_MEM_W / 8)         /* 100 bytes per RAM row       */
#define EPD_GATE_BITS   272                     /* rows per controller         */
#define EPD_HALF_SOURCE_BYTES (400 / 8)         /* 50 bytes/row per controller */
#define EPD_HALF_BYTES  (EPD_HALF_SOURCE_BYTES * EPD_GATE_BITS)  /* 13600/half  */
#define EPD_FULL_BUF_SIZE (EPD_ROW_BYTES * EPD_GATE_BITS)        /* 27200       */

/* Pixel values in the 1-bit buffer: 1 = white, 0 = black (SSD1683 B/W RAM). */
#define EPD_WHITE       0xFF
#define EPD_BLACK       0x00

/* -- 4-gray pixel codes (2 bits per pixel, matches the 4.2" epd_4in2 encoding) --
 * Plane mapping: bit in 0x24 plane = code & 1, bit in 0x26 plane = (code >> 1) & 1:
 *   3 = white      (24:1 26:1)
 *   2 = light grey (24:0 26:1)
 *   1 = dark grey  (24:1 26:0)
 *   0 = black      (24:0 26:0)                                                  */
#define EPD_G_BLACK     0
#define EPD_G_DARK      1
#define EPD_G_LIGHT     2
#define EPD_G_WHITE     3

/* -- Shared pins (same physical wiring as the 4.2", confirmed in vendor spi.h) */
#define EPD_PIN_SCK     12
#define EPD_PIN_MOSI    11
#define EPD_PIN_RES     47
#define EPD_PIN_DC      46
#define EPD_PIN_CS      45
#define EPD_PIN_BUSY    48
#define EPD_PIN_PWR     7       /* power-enable: must be HIGH or the panel is unpowered */

/** @brief Configure the EPD GPIOs (SPI bit-bang + BUSY + power-enable). Powers the panel. */
void epd_gpio_init(void);

/** @brief Reset + soft-reset the cascade (full-refresh init). */
void epd_init(void);

/** @brief Clear the whole panel to white (full refresh, both halves). */
void epd_clear(void);

/**
 * @brief Write a full 800x272 frame to both halves and trigger ONE full refresh.
 * @param image EPD_FULL_BUF_SIZE bytes, row-major, 100 bytes/row, MSB = leftmost.
 *              Build it with epd_set_pixel() so the gap/mirror mapping is applied.
 */
void epd_display(const uint8_t *image);

/**
 * @brief Set one logical pixel (0..791, 0..271) in a 800x272 buffer, applying the
 *        8px gap skip and the Rotation-180 mirror so it lands in the right RAM cell.
 * @param buf   EPD_FULL_BUF_SIZE buffer.
 * @param x,y   Logical coordinates in the visible 792x272 area.
 * @param black Non-zero = black pixel, zero = white.
 */
void epd_set_pixel(uint8_t *buf, int x, int y, int black);

/* -- 4-gray, Waveshare-exact port (EPD_5in79.c) ------------------------------
 * Probes v1-v3 (our 4.2" LUT + own geometry) produced tones on the master only,
 * with broken scan geometry. Waveshare ships a PROVEN 4-gray driver for this
 * same dual-SSD1683 792x272 glass; v4 ports it verbatim:
 *   - init programs BOTH controllers' windows/cursors once (master 0x11=0x01
 *     row-major, slave 0x91=0x00 = its mirrored data entry), no 0x01 MUX writes;
 *   - the image is a plain LINEAR 2bpp logical buffer (792x272, 4 px/byte,
 *     stride 198 B/row) — the hardware handles the mirror and the seam overlap
 *     (master reads row bytes 0..99, slave reads bytes 98..197);
 *   - four full-plane row-major streams (0x24, 0x26, 0xA4, 0xA6), no cursor
 *     touches in between (the window wraps), then 0x22 = 0xCF.               */

/* Linear 2bpp logical image: 4 pixels/byte, MSB first. 198 B/row x 272 rows. */
#define EPD_G2_STRIDE   (2 * ((EPD_LOGICAL_W + 7) / 8))       /* 198 bytes    */
#define EPD_G2_BUF_SIZE (EPD_G2_STRIDE * EPD_LOGICAL_H)       /* 53856 bytes  */

/** @brief Waveshare-exact 4-gray init (booster, border 0x81, both windows,
 *         grayscale LUT + EOPT/VGH/VSH/VSL/VCOM tail). Call after epd_gpio_init(). */
void epd_init_4gray_ws(void);

/**
 * @brief Set one logical pixel (0..791, 0..271) in the linear 2bpp image.
 * @param img  EPD_G2_BUF_SIZE buffer.
 * @param code EPD_G_BLACK / EPD_G_DARK / EPD_G_LIGHT / EPD_G_WHITE.
 */
void epd_g2_set_pixel(uint8_t *img, int x, int y, uint8_t code);

/** @brief Waveshare-exact 4-gray display: streams all four planes from the
 *         linear 2bpp image and triggers one 0xCF refresh. */
void epd_display_4gray_ws(const uint8_t *img);

/** @brief Put both controllers into deep sleep (holds the image at ~uA). */
void epd_sleep(void);
