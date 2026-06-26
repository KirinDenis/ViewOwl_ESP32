/**
 * @file epd_4in2.h
 * @brief Clean ESP-IDF driver for the 400x300 4.2" e-paper (SSD1680-family).
 *
 * Written from an understanding of the controller (the vendor Arduino sketch was a
 * reference only, never copied): bit-banged SPI, command/data split on DC, BUSY-poll,
 * 1-bit framebuffer (8 px/byte, 50 bytes/row x 300 rows = 15000 bytes), full / fast
 * refresh, deep sleep.
 */
#pragma once

#include <stdint.h>

/* Panel geometry. */
#define EPD_W           400
#define EPD_H           300
#define EPD_ROW_BYTES   (EPD_W / 8)              /* 50 */
#define EPD_BUF_SIZE    (EPD_ROW_BYTES * EPD_H)  /* 15000 */

/* Pixel values in the 1-bit buffer: 1 = white, 0 = black (SSD1680 B/W RAM). */
#define EPD_WHITE       0xFF
#define EPD_BLACK       0x00

/* 4-gray ("mono CGA") framebuffer: 2 bits/pixel, 4 levels.
 * Per-pixel code (MSB-first): 0b11 = white, 0b10 = light grey, 0b01 = dark grey,
 * 0b00 = black. Size = 2 x the 1-bit buffer. */
#define EPD_BUF_SIZE_4GRAY  (EPD_ROW_BYTES * 2 * EPD_H)   /* 30000 */

/** @brief Configure the EPD GPIOs (SPI bit-bang lines + BUSY + power-enable). */
void epd_gpio_init(void);

/** @brief Reset + initialise the controller for a full refresh. */
void epd_init(void);

/** @brief Clear the panel to white (full refresh). Calls epd_init() conventions. */
void epd_clear(void);

/**
 * @brief Write a full 1-bit frame and trigger a full refresh.
 * @param image Pointer to EPD_BUF_SIZE bytes, row-major, MSB = leftmost pixel.
 */
void epd_display(const uint8_t *image);

/**
 * @brief Partial refresh of a region — updates without the full-panel flash.
 *        Needs a prior full refresh as a baseline. Whole-screen pass: x=y=0, w=EPD_W, h=EPD_H.
 *        Does NOT reset/clear the controller (that is the point — push only changes).
 * @param x,y   Top-left of the region.
 * @param w,h   Region size in pixels (w should be a multiple of 8).
 * @param image Region buffer, row stride = ceil(w/8) bytes.
 */
void epd_display_part(uint16_t x, uint16_t y, uint16_t w, uint16_t h, const uint8_t *image);

/** @brief Reset + initialise for 4-gray mode (loads the grayscale waveform LUT). */
void epd_init_4gray(void);

/**
 * @brief Display a 2-bits-per-pixel frame in 4 grey levels (full refresh).
 * @param image EPD_BUF_SIZE_4GRAY bytes, 2 bits/pixel (see the code table above).
 */
void epd_display_4gray(const uint8_t *image);

/**
 * @brief EXPERIMENT: partial refresh of a region in 4 grey levels.
 *        Mirrors epd_display_4gray but writes only a window (both planes) and
 *        triggers the grey LUT update. Needs a prior epd_init_4gray + a 4-gray
 *        full refresh as baseline. Open question: whether the grey waveform
 *        respects the window or flashes the whole panel / ghosts the edges.
 * @param x,y   Top-left (x should be a multiple of 8).
 * @param w,h   Region size in pixels (w a multiple of 8).
 * @param image Region buffer, 2 bits/pixel, row stride = ceil(w/4) bytes.
 */
void epd_display_part_4gray(uint16_t x, uint16_t y, uint16_t w, uint16_t h, const uint8_t *image);

/** @brief Put the controller into deep sleep (holds the image at ~uA). */
void epd_sleep(void);
