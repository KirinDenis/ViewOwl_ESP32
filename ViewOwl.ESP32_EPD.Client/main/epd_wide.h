/**
 * @file epd_wide.h
 * @brief WIDE 792x272 e-paper driver — Elecrow CrowPanel 5.79" (two SSD1683 ICs in a
 *        master/slave cascade on ONE SPI bus / ONE chip-select).
 *
 * API-compatible drop-in for epd_4in2.h: the SAME macros (EPD_W/EPD_H/EPD_ROW_BYTES/
 * EPD_BUF_SIZE) and functions (epd_gpio_init/epd_init/epd_display/...), so main.c,
 * udp_client.c and epd_text.c build unchanged — selected by the EPD_792x272 build flag
 * via epd_panel.h. The server frame is a LOGICAL 792x272 1-bit image (99 bytes/row);
 * epd_display() internally remaps it into the cascade's 800x272 RAM (8px inter-panel
 * gap + Rotation-180 mirror + master/slave split). See the diagnostics-wide polygon and
 * the Elecrow vendor sequence for the derivation.
 *
 * v0.3.0: full-refresh mono 1-bit AND 4-gray (verbatim Waveshare EPD_5in79 port,
 * verified on hardware by the diagnostics-wide probe v4). True partial refresh is
 * still not ported; partial entry points fall back to full refresh / no-op.
 */
#pragma once

#include <stdint.h>

/* -- Logical geometry (matches the server's mono1bit frame for 792x272) ----- */
#define EPD_W           792
#define EPD_H           272
#define EPD_ROW_BYTES   (EPD_W / 8)              /* 99 */
#define EPD_BUF_SIZE    (EPD_ROW_BYTES * EPD_H)  /* 26928 */

/* Pixel values in the 1-bit buffer: 1 = white, 0 = black (SSD1683 B/W RAM). */
#define EPD_WHITE       0xFF
#define EPD_BLACK       0x00

/* 4-gray frame: linear 2bpp, 4 px/byte MSB-first, 198 bytes/row (53856 total),
 * codes 3=white 2=light-grey 1=dark-grey 0=black — the server's mono4gray format. */
#define EPD_BUF_SIZE_4GRAY  (EPD_ROW_BYTES * 2 * EPD_H)

/** @brief Configure the EPD GPIOs (SPI bit-bang lines + BUSY + power-enable). */
void epd_gpio_init(void);

/** @brief Reset + soft-reset the cascade (full-refresh init). */
void epd_init(void);

/** @brief Clear the whole panel to white (full refresh, both halves). */
void epd_clear(void);

/**
 * @brief Write a full LOGICAL 792x272 1-bit frame and trigger ONE full refresh.
 * @param image EPD_BUF_SIZE bytes, row-major, 99 bytes/row, MSB = leftmost, 1 = white.
 */
void epd_display(const uint8_t *image);

/**
 * @brief Partial-refresh entry point (signature-compatible with epd_4in2.h).
 *        v1 cascade has no true partial: a whole-screen call (x=y=0, w=EPD_W, h=EPD_H)
 *        falls back to a full refresh of @p image; any sub-region call is a no-op.
 * @param x,y,w,h Region (only the whole-screen region is honoured in v1).
 * @param image   Frame buffer for the whole-screen case.
 */
void epd_display_part(uint16_t x, uint16_t y, uint16_t w, uint16_t h, const uint8_t *image);

/** @brief 4-gray init: Waveshare-exact cascade sequence + grayscale register LUT. */
void epd_init_4gray(void);

/**
 * @brief Full 4-gray display from the server's linear 2bpp frame
 *        (EPD_BUF_SIZE_4GRAY bytes). Requires a prior epd_init_4gray().
 */
void epd_display_4gray(const uint8_t *image);

/** @brief 4-gray partial: whole-screen falls back to a full 4-gray refresh,
 *         sub-region calls are no-ops (no true partial on the cascade yet). */
void epd_display_part_4gray(uint16_t x, uint16_t y, uint16_t w, uint16_t h, const uint8_t *image);

/** @brief Put both controllers into deep sleep (holds the image at ~uA). */
void epd_sleep(void);
