#pragma once
#include <stdint.h>

/**
 * @brief RGB565 color constants for the ST7796 LCD.
 *
 * The SPI controller sends bytes low-address-first (little-endian), and the
 * panel treats the first received byte as the MSB of the 16-bit pixel value.
 * Constants are stored as bswap16(intended_panel_value) so that after the SPI
 * byte reversal the panel reconstructs the correct pixel.
 *
 * Empirical calibration shows the panel operates as RGB565:
 *   bits[15:11] → Red, bits[10:5] → Green, bits[4:0] → Blue.
 *
 * Derivation example for YELLOW (R=31, G=63, B=0, panel value 0xFFE0):
 *   bswap16(0xFFE0) = 0xE0FF → memory [0xFF,0xE0] → SPI 0xFF,0xE0 → panel 0xFFE0 ✓
 */
#define LCD_COLOR_BLACK   ((uint16_t)0x0000)  /* panel sees 0x0000 → R=0,G=0,B=0    */
#define LCD_COLOR_WHITE   ((uint16_t)0xFFFF)  /* panel sees 0xFFFF → R=31,G=63,B=31 */
#define LCD_COLOR_RED     ((uint16_t)0x00F8)  /* panel sees 0xF800 → R=31            */
#define LCD_COLOR_GREEN   ((uint16_t)0xE007)  /* panel sees 0x07E0 → G=63            */
#define LCD_COLOR_BLUE    ((uint16_t)0x1F00)  /* panel sees 0x001F → B=31            */
#define LCD_COLOR_YELLOW  ((uint16_t)0xE0FF)  /* panel sees 0xFFE0 → R=31,G=63       */
#define LCD_COLOR_CYAN    ((uint16_t)0xFF07)  /* panel sees 0x07FF → G=63,B=31       */
#define LCD_COLOR_DARK    ((uint16_t)0x4108)  /* panel sees 0x0841 → dark gray       */
#define LCD_COLOR_DIM     ((uint16_t)0x0842)  /* panel sees 0x4208 → mid gray        */

/**
 * @brief Fills a rectangular region with a solid color.
 *
 * @param x      Left edge (pixels, 0-based).
 * @param y      Top edge (pixels, 0-based).
 * @param w      Width in pixels.
 * @param h      Height in pixels.
 * @param color  BGR565 fill color.
 */
void lcd_fill_rect(int x, int y, int w, int h, uint16_t color);

/**
 * @brief Draws a single ASCII character using the 8×8 bitmap font.
 *
 * @param x   Left edge of the glyph cell.
 * @param y   Top edge of the glyph cell.
 * @param c   ASCII character (32–127; out-of-range chars rendered as '?').
 * @param fg  Foreground BGR565 color.
 * @param bg  Background BGR565 color.
 */
void lcd_draw_char(int x, int y, char c, uint16_t fg, uint16_t bg);

/**
 * @brief Draws a NUL-terminated string left-to-right using the 8×8 font.
 *
 * Characters that would exceed the screen width are clipped.
 *
 * @param x   Starting X position.
 * @param y   Starting Y position.
 * @param str NUL-terminated ASCII string.
 * @param fg  Foreground BGR565 color.
 * @param bg  Background BGR565 color.
 */
void lcd_draw_string(int x, int y, const char *str, uint16_t fg, uint16_t bg);

/**
 * @brief Fills a rectangular region with a tiled 1-bit pattern.
 *
 * The pattern is an array of bytes (MSB-left, 8 pixels wide) that tiles
 * horizontally and vertically.  Uses the internal strip buffer so the
 * entire area is blitted in ~(h / pat_h) SPI transactions — orders of
 * magnitude faster than per-pixel lcd_fill_rect calls.
 *
 * @param x      Left edge (pixels).
 * @param y      Top edge (pixels).
 * @param w      Width in pixels.
 * @param h      Height in pixels.
 * @param pat    1-bit pattern rows (MSB = leftmost pixel), 8 pixels wide.
 * @param pat_h  Number of rows in the pattern (typically 8).
 * @param fg     Foreground colour (set bits).
 * @param bg     Background colour (clear bits).
 */
void lcd_fill_pattern(int x, int y, int w, int h,
                      const uint8_t *pat, int pat_h,
                      uint16_t fg, uint16_t bg);
