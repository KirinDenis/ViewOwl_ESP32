#include "lcd_text.h"
#include "lcd_font.h"
#include "config.h"

#include <string.h>

#include "esp_lcd_panel_io.h"
#include "esp_lcd_panel_commands.h"

#ifdef _ILI9486_LCD
#include "esp_lcd_ili9486.h"
#else
/* io_handle is declared in lcd_init.c and used here for raw SPI transactions. */
extern esp_lcd_panel_io_handle_t io_handle;
#endif

/* ── Static line buffer (BSS) ─────────────────────────────────────────────
 * Large enough for one horizontal strip: full width × FONT_H rows.
 * Keeping it static avoids per-call heap allocation.
 */
#define FONT_W 8
#define FONT_H 8

/* Maximum pixels in one fill call: full-width, 8-pixel-tall strip. */
#define LINE_BUF_PIXELS (LCD_H_RES * FONT_H)

static uint16_t s_strip_buf[LINE_BUF_PIXELS];

/* ── Internal SPI helpers ──────────────────────────────────────────────── */

/**
 * @brief Sets the LCD address window and blits a pixel buffer via RAMWR.
 *
 * For the ILI9486 build variant uses the raw 16-bit SPI path (ili9486_blit).
 * For ILI9341 / ST7796 variants uses the esp_lcd io_handle path, with a
 * trailing NOP to drain the SPI queue before the next command.
 *
 * @param x1     Left column (inclusive).
 * @param y1     Top row (inclusive).
 * @param x2     Right column (exclusive).
 * @param y2     Bottom row (exclusive).
 * @param pixels BGR565 pixel data (must be (x2-x1)*(y2-y1) elements).
 * @param size   Byte count of pixel data.
 */
static void blit(int x1, int y1, int x2, int y2,
                 const uint16_t *pixels, size_t size)
{
#ifdef _ILI9486_LCD
    /* ILI9486: raw 16-bit SPI — bypasses io_handle (8-bit, incompatible with
     * 74HC245 level-shifter buffers on Waveshare RPi LCD (A) V3).
     * Pixel data is sent in memory order; DMA does not byte-reverse. */
    ili9486_blit(x1, y1, x2, y2, (const uint8_t *)pixels, size);
#else
    ESP_ERROR_CHECK(esp_lcd_panel_io_tx_param(io_handle, LCD_CMD_CASET,
        (uint8_t[]){
            (x1 >> 8) & 0xFF, x1 & 0xFF,
            ((x2 - 1) >> 8) & 0xFF, (x2 - 1) & 0xFF,
        }, 4));

    ESP_ERROR_CHECK(esp_lcd_panel_io_tx_param(io_handle, LCD_CMD_RASET,
        (uint8_t[]){
            (y1 >> 8) & 0xFF, y1 & 0xFF,
            ((y2 - 1) >> 8) & 0xFF, (y2 - 1) & 0xFF,
        }, 4));

    ESP_ERROR_CHECK(esp_lcd_panel_io_tx_color(io_handle, LCD_CMD_RAMWR,
        pixels, size));

    /* Synchronisation NOP — ensures the SPI peripheral has finished. */
    ESP_ERROR_CHECK(esp_lcd_panel_io_tx_param(io_handle, 0x00, NULL, 0));
#endif
}

/* ── Public API ────────────────────────────────────────────────────────── */

void lcd_fill_rect(int x, int y, int w, int h, uint16_t color)
{
    if (w <= 0 || h <= 0) return;

    /* Clamp to display bounds. */
    if (x < 0) { w += x; x = 0; }
    if (y < 0) { h += y; y = 0; }
    if (x + w > LCD_H_RES) w = LCD_H_RES - x;
    if (y + h > LCD_V_RES) h = LCD_V_RES - y;
    if (w <= 0 || h <= 0) return;

    /* Fill in strips of up to FONT_H rows so s_strip_buf is always large enough. */
    int rows_per_strip = LINE_BUF_PIXELS / w;
    if (rows_per_strip < 1) rows_per_strip = 1;

    int rows_done = 0;
    while (rows_done < h) {
        int rows = h - rows_done;
        if (rows > rows_per_strip) rows = rows_per_strip;

        int count = w * rows;
        for (int i = 0; i < count; i++) s_strip_buf[i] = color;

        blit(x, y + rows_done, x + w, y + rows_done + rows,
             s_strip_buf, (size_t)count * sizeof(uint16_t));

        rows_done += rows;
    }
}

void lcd_fill_pattern(int x, int y, int w, int h,
                      const uint8_t *pat, int pat_h,
                      uint16_t fg, uint16_t bg)
{
    if (w <= 0 || h <= 0 || !pat || pat_h <= 0) return;

    if (x < 0) { w += x; x = 0; }
    if (y < 0) { h += y; y = 0; }
    if (x + w > LCD_H_RES) w = LCD_H_RES - x;
    if (y + h > LCD_V_RES) h = LCD_V_RES - y;
    if (w <= 0 || h <= 0) return;

    int rows_per_strip = LINE_BUF_PIXELS / w;
    if (rows_per_strip > pat_h) rows_per_strip = pat_h;
    if (rows_per_strip < 1) rows_per_strip = 1;

    int rows_done = 0;
    while (rows_done < h) {
        int chunk = h - rows_done;
        if (chunk > rows_per_strip) chunk = rows_per_strip;

        for (int row = 0; row < chunk; row++) {
            int pat_row = (y + rows_done + row) % pat_h;
            uint8_t bits = pat[pat_row];
            uint16_t *dst = &s_strip_buf[row * w];
            for (int col = 0; col < w; col++) {
                int pat_col = (x + col) % 8;
                dst[col] = (bits & (0x80u >> pat_col)) ? fg : bg;
            }
        }

        blit(x, y + rows_done, x + w, y + rows_done + chunk,
             s_strip_buf, (size_t)(chunk * w) * sizeof(uint16_t));

        rows_done += chunk;
    }
}

void lcd_draw_char(int x, int y, char c, uint16_t fg, uint16_t bg)
{
    if (x + FONT_W > LCD_H_RES || y + FONT_H > LCD_V_RES) return;
    if ((uint8_t)c < 32 || (uint8_t)c > 127) c = '?';

    const uint8_t *glyph = LCD_FONT_8X8[(uint8_t)c - 32];
    uint16_t pixels[FONT_W * FONT_H];

    for (int row = 0; row < FONT_H; row++) {
        uint8_t bits = glyph[row];
        for (int col = 0; col < FONT_W; col++) {
            pixels[row * FONT_W + col] = (bits & (0x80u >> col)) ? fg : bg;
        }
    }

    blit(x, y, x + FONT_W, y + FONT_H, pixels, sizeof(pixels));
}

void lcd_draw_string(int x, int y, const char *str, uint16_t fg, uint16_t bg)
{
    if (!str) return;
    int cx = x;
    for (int i = 0; str[i] != '\0'; i++) {
        if (cx + FONT_W > LCD_H_RES) break;
        lcd_draw_char(cx, y, str[i], fg, bg);
        cx += FONT_W;
    }
}
