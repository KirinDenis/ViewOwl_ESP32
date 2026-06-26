/**
 * @file epd_text.h
 * @brief 1-bit text rendering for the e-paper framebuffer.
 *
 * Uses the shared 8x8 ASCII bitmap font (lcd_font.h from the classic client) and
 * blits glyphs into a mono framebuffer (1 = white, 0 = black; MSB = leftmost pixel,
 * EPD_ROW_BYTES per row) — the same layout epd_display()/epd_display_part() expect.
 */
#pragma once
#include <stdint.h>

/**
 * @brief Draw one ASCII glyph (8x8) into a mono framebuffer.
 * @param buf   Framebuffer (EPD_BUF_SIZE bytes).
 * @param x,y   Top-left pixel of the glyph.
 * @param c     ASCII character (32..127; others render as '?').
 * @param black Non-zero draws black pixels, zero draws white (erase).
 */
void epd_draw_char(uint8_t *buf, int x, int y, char c, int black);

/** @brief Draw a NUL-terminated string left-to-right, 8 px per glyph. */
void epd_draw_text(uint8_t *buf, int x, int y, const char *s, int black);
