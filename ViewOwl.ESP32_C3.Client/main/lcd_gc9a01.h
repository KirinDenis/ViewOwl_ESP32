#pragma once

#include <stdint.h>

/**
 * @file lcd_gc9a01.h
 * @brief GC9A01 round 240x240 display driver for the Elecrow CrowPanel 1.28"
 *        (ESP32-C3). Reset + backlight are driven via a PI4IOE5V6408 I2C
 *        expander; pixel data goes over SPI. Bring-up proven by the diagnostic
 *        (diagnostics/gc9a01). Default orientation MADCTL=0x28 (USB-C on side).
 */

/** Initialise I2C expander (RST + backlight), SPI bus, and the GC9A01 panel. */
void lcd_gc9a01_init(void);

/** Fill the whole screen with one RGB565 colour. */
void lcd_gc9a01_fill(uint16_t colour);

/** Fill a rectangle [x,y,w,h] with one RGB565 colour. */
void lcd_gc9a01_fill_rect(int x, int y, int w, int h, uint16_t colour);

/** Scanline-filled circle (centre cx,cy, radius r). */
void lcd_gc9a01_fill_circle(int cx, int cy, int r, uint16_t colour);

/** Blit a wxh block of RGB565 pixels (row-major) at (x,y). */
void lcd_gc9a01_blit(int x, int y, int w, int h, const uint16_t *pixels);

/**
 * Blit a wxh block of BIG-ENDIAN RGB565 bytes (2 B/pixel, high byte first) at (x,y).
 * Sent to the panel as-is, without the per-pixel swap lcd_gc9a01_blit does - use this
 * for the frame decoder output (frame_lz4_stream_read), which already emits wire-order
 * big-endian pixels. @p be_pixels holds w*h*2 bytes.
 */
void lcd_gc9a01_blit_be(int x, int y, int w, int h, const uint8_t *be_pixels);
