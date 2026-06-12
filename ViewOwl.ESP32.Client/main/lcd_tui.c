#include "lcd_tui.h"
#include "lcd_font.h"
#include "lcd_text.h"
#include "config.h"

#include <string.h>
#include <stddef.h>
#include <stdio.h>
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

/* ── Internal helpers ─────────────────────────────────────────────────── */

/* Convert cell column to pixel X. */
static inline int cx2px(int cx) { return cx * TUI_CELL_W; }

/* Convert cell row to pixel Y. */
static inline int cy2px(int cy) { return cy * TUI_CELL_H; }

/* Total cells horizontally on this display variant. */
static inline int cols(void) { return LCD_H_RES / TUI_CELL_W; }

/* Total cells vertically on this display variant. */
static inline int rows(void) { return LCD_V_RES / TUI_CELL_H; }

/* ── Glyph renderer ───────────────────────────────────────────────────── */

/**
 * @brief Renders one TUI glyph bitmap at pixel position (px, py).
 *
 * Identical raster loop to lcd_draw_char() in lcd_text.c but reads from
 * LCD_TUI_GLYPHS instead of LCD_FONT_8X8.
 */
static void render_tui_glyph_px(int px, int py, int glyph_idx,
                                  uint16_t fg, uint16_t bg)
{
    if (glyph_idx < 0 || glyph_idx >= TUI_GLYPH_COUNT) return;

    /* Build an 8×8 pixel strip and blit via lcd_draw_char workaround:
     * we re-use lcd_fill_rect + individual pixel trick by drawing each row
     * as a 8-wide, 1-tall rectangle for the two colours.
     * A simpler approach: just call lcd_draw_char with a placeholder and
     * overdraw — but that would corrupt the glyph.  Instead we call
     * lcd_fill_rect for the background then draw foreground pixels only.
     *
     * Fastest correct approach within the existing HAL: draw the cell as
     * background, then draw each set bit as a 1×1 pixel via a 1-px fill. */
    lcd_fill_rect(px, py, TUI_CELL_W, TUI_CELL_H, bg);

    const uint8_t *bmp = LCD_TUI_GLYPHS[glyph_idx];
    for (int row = 0; row < TUI_CELL_H; row++) {
        uint8_t bits = bmp[row];
        for (int col = 0; col < TUI_CELL_W; col++) {
            if (bits & (0x80u >> col)) {
                lcd_fill_rect(px + col, py + row, 1, 1, fg);
            }
        }
    }
}

/* ── ░ shade pattern (shared by desktop and shadow) ───────────────────── */

static const uint8_t s_shade_pattern[8] = {
    0x22, 0x00, 0x88, 0x00,   /* ..#...#.  ........  #...#...  ........ */
    0x22, 0x00, 0x88, 0x00,
};

/* ── Public API ───────────────────────────────────────────────────────── */

void lcd_tui_draw_desktop(void)
{
    /* ░ (light shade) via lcd_fill_pattern — ~40 strip-blits, ~8 ms total. */
    lcd_fill_pattern(0, 0, LCD_H_RES, LCD_V_RES,
                     s_shade_pattern, 8,
                     TUI_COLOR_DESKTOP_FG, TUI_COLOR_DESKTOP_BG);
}

void lcd_tui_fill(int cx, int cy, int cw, int ch, uint16_t color)
{
    lcd_fill_rect(cx2px(cx), cy2px(cy),
                  cw * TUI_CELL_W, ch * TUI_CELL_H,
                  color);
}

void lcd_tui_draw_glyph(int cx, int cy, int glyph, uint16_t fg, uint16_t bg)
{
    render_tui_glyph_px(cx2px(cx), cy2px(cy), glyph, fg, bg);
}

void lcd_tui_draw_char(int cx, int cy, char c, uint16_t fg, uint16_t bg)
{
    lcd_draw_char(cx2px(cx), cy2px(cy), c, fg, bg);
}

void lcd_tui_print(int cx, int cy, const char *str, uint16_t fg, uint16_t bg)
{
    if (!str) return;
    int col = cx;
    int max_col = cols();
    while (*str && col < max_col) {
        lcd_draw_char(cx2px(col), cy2px(cy), *str, fg, bg);
        col++;
        str++;
    }
}

void lcd_tui_print_centered(int cx, int cy, int cw,
                              const char *str, uint16_t fg, uint16_t bg)
{
    if (!str) return;
    int len = (int)strlen(str);
    if (len > cw) len = cw;
    int pad = (cw - len) / 2;
    /* Fill background for the whole span first. */
    lcd_tui_fill(cx, cy, cw, 1, bg);
    lcd_tui_print(cx + pad, cy, str, fg, bg);
}

/* ── Window / popup helpers ──────────────────────────────────────────────
 *
 * Borders are drawn as 1-pixel lcd_fill_rect lines — ~6 SPI calls total
 * instead of dozens of per-cell glyph renders.
 */
static void draw_box(int cx, int cy, int cw, int ch,
                     uint16_t border_fg, uint16_t bg,
                     const char *title, uint16_t title_fg,
                     bool shadow_on_desktop)
{
    int px = cx2px(cx);
    int py = cy2px(cy);
    int pw = cw * TUI_CELL_W;
    int ph = ch * TUI_CELL_H;

    /* Shadow: 1 cell right + 1 cell below. */
    if (shadow_on_desktop) {
        /* Desktop shadow: ░ dots (cyan on black) — desktop pattern dimmed. */
        lcd_fill_pattern(px + TUI_CELL_W, py + ph,
                         pw, TUI_CELL_H,
                         s_shade_pattern, 8,
                         TUI_COLOR_SHADOW_FG, TUI_COLOR_SHADOW_BG);
        lcd_fill_pattern(px + pw, py + TUI_CELL_H,
                         TUI_CELL_W, ph - TUI_CELL_H,
                         s_shade_pattern, 8,
                         TUI_COLOR_SHADOW_FG, TUI_COLOR_SHADOW_BG);
    } else {
        /* Popup shadow on window: solid black. */
        lcd_fill_rect(px + TUI_CELL_W, py + ph,
                      pw, TUI_CELL_H, TUI_COLOR_SHADOW_BG);
        lcd_fill_rect(px + pw, py + TUI_CELL_H,
                      TUI_CELL_W, ph - TUI_CELL_H, TUI_COLOR_SHADOW_BG);
    }

    /* Interior fill — entire box area. */
    lcd_fill_rect(px, py, pw, ph, bg);

    if (shadow_on_desktop) {
        /* Window: double border — two 1-px lines with 1-px gap (╔═╗). */
        lcd_fill_rect(px,          py,          pw, 1,  border_fg);  /* top outer    */
        lcd_fill_rect(px,          py + 2,      pw, 1,  border_fg);  /* top inner    */
        lcd_fill_rect(px,          py + ph - 1, pw, 1,  border_fg);  /* bottom outer */
        lcd_fill_rect(px,          py + ph - 3, pw, 1,  border_fg);  /* bottom inner */
        lcd_fill_rect(px,          py,          1,  ph, border_fg);  /* left outer   */
        lcd_fill_rect(px + 2,      py,          1,  ph, border_fg);  /* left inner   */
        lcd_fill_rect(px + pw - 1, py,          1,  ph, border_fg);  /* right outer  */
        lcd_fill_rect(px + pw - 3, py,          1,  ph, border_fg);  /* right inner  */
    } else {
        /* Popup: single border — 1-px lines (┌─┐). */
        lcd_fill_rect(px,          py,          pw, 1,  border_fg);  /* top    */
        lcd_fill_rect(px,          py + ph - 1, pw, 1,  border_fg);  /* bottom */
        lcd_fill_rect(px,          py,          1,  ph, border_fg);  /* left   */
        lcd_fill_rect(px + pw - 1, py,          1,  ph, border_fg);  /* right  */
    }

    /* Title centred in the top border. */
    if (title && cw > 4) {
        char padded[64];
        int max_title = cw - 4;
        snprintf(padded, sizeof(padded), " %.*s ", max_title, title);
        int tlen = (int)strlen(padded);
        int tstart = cx + (cw - tlen) / 2;
        for (int i = 0; i < tlen && (tstart + i) < (cx + cw - 1); i++) {
            lcd_tui_draw_char(tstart + i, cy, padded[i], title_fg, bg);
        }
    }
}

void lcd_tui_draw_window(int cx, int cy, int cw, int ch,
                          const char *title, uint16_t bg_color)
{
    draw_box(cx, cy, cw, ch,
             TUI_COLOR_WIN_BORDER, bg_color,
             title, TUI_COLOR_WIN_TITLE,
             true);
}

void lcd_tui_draw_popup(int cx, int cy, int cw, int ch, const char *title)
{
    draw_box(cx, cy, cw, ch,
             TUI_COLOR_POPUP_BORDER, TUI_COLOR_POPUP_BG,
             title, TUI_COLOR_POPUP_TITLE,
             false);
}

void lcd_tui_draw_progress(int cx, int cy, int cw,
                            int value, int max, uint16_t bg_color)
{
    if (max <= 0) max = 1;
    if (value < 0) value = 0;
    if (value > max) value = max;

    int filled = (value * cw) / max;
    int px = cx2px(cx);
    int py = cy2px(cy);

    /* Filled portion — solid green. */
    if (filled > 0) {
        lcd_fill_rect(px, py,
                      filled * TUI_CELL_W, TUI_CELL_H,
                      TUI_COLOR_BAR_FILL);
    }
    /* Empty track — dim. */
    if (filled < cw) {
        lcd_fill_rect(px + filled * TUI_CELL_W, py,
                      (cw - filled) * TUI_CELL_W, TUI_CELL_H,
                      TUI_COLOR_BAR_EMPTY);
    }
}
