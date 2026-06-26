/**
 * @file epd_text.c
 * @brief 1-bit ASCII text rendering into the e-paper mono framebuffer.
 */
#include "epd_text.h"
#include "epd_4in2.h"   /* EPD_W, EPD_H, EPD_ROW_BYTES */
#include "lcd_font.h"   /* LCD_FONT_8X8 — shared from the classic client (on the include path) */

/* Set one pixel in the mono framebuffer (1 = white, 0 = black). */
static inline void set_px(uint8_t *buf, int x, int y, int black)
{
    if (x < 0 || x >= EPD_W || y < 0 || y >= EPD_H) {
        return;
    }
    int idx = y * EPD_ROW_BYTES + (x >> 3);
    uint8_t bit = (uint8_t)(0x80 >> (x & 7));
    if (black) {
        buf[idx] &= (uint8_t)~bit;
    } else {
        buf[idx] |= bit;
    }
}

void epd_draw_char(uint8_t *buf, int x, int y, char c, int black)
{
    if (c < 32 || c > 127) {
        c = '?';
    }
    const uint8_t *glyph = LCD_FONT_8X8[(int)c - 32];
    for (int row = 0; row < 8; row++) {
        uint8_t bits = glyph[row];
        for (int col = 0; col < 8; col++) {
            if (bits & (0x80 >> col)) {
                set_px(buf, x + col, y + row, black);
            }
        }
    }
}

void epd_draw_text(uint8_t *buf, int x, int y, const char *s, int black)
{
    for (; *s != '\0'; s++) {
        epd_draw_char(buf, x, y, *s, black);
        x += 8;
    }
}
