#pragma once

#include <stdint.h>
#include <stdbool.h>
#include "lcd_text.h"

/* ── TUI colour palette (Turbo Vision inspired) ───────────────────────────
 *
 * All values use the same bswap16 convention as lcd_text.h:
 *   stored = bswap16(panel_rgb565_value)
 *
 * Desktop:   dark cyan stipple background (░ pattern)
 * Window:    bright blue fill, white/yellow text
 * Popup:     light gray fill, black text
 * Shadow:    dark gray solid — 1 cell right + 1 cell below the window
 * Progress:  filled block vs empty block inside a window
 */
#define TUI_COLOR_DESKTOP_FG   ((uint16_t)0xFF07)  /* cyan  — ░ glyph fg  */
#define TUI_COLOR_DESKTOP_BG   ((uint16_t)0x1500)  /* classic EGA blue — desktop bg */

#define TUI_COLOR_WIN_BG       ((uint16_t)0x1500)  /* classic EGA blue — window bg */
#define TUI_COLOR_WIN_BORDER   ((uint16_t)0xFFFF)  /* white border / frame    */
#define TUI_COLOR_WIN_TITLE    ((uint16_t)0xE0FF)  /* yellow title text       */
#define TUI_COLOR_WIN_TEXT     ((uint16_t)0xFFFF)  /* white body text         */
#define TUI_COLOR_WIN_DIM      ((uint16_t)0xFF07)  /* cyan dim text           */

#define TUI_COLOR_POPUP_BG     ((uint16_t)0x55AD)  /* EGA light gray popup bg */
#define TUI_COLOR_POPUP_BORDER ((uint16_t)0xFFFF)  /* white border            */
#define TUI_COLOR_POPUP_TITLE  ((uint16_t)0xE0FF)  /* yellow title            */
#define TUI_COLOR_POPUP_TEXT   ((uint16_t)0xFFFF)  /* white body text         */
#define TUI_COLOR_POPUP_OK     ((uint16_t)0xE007)  /* green OK / success      */
#define TUI_COLOR_POPUP_WARN   ((uint16_t)0xE0FF)  /* yellow warning          */
#define TUI_COLOR_POPUP_ERR    ((uint16_t)0x00F8)  /* red error               */

#define TUI_COLOR_SHADOW_FG    ((uint16_t)0x2C03)  /* dim cyan — shadow dots  */
#define TUI_COLOR_SHADOW       ((uint16_t)0x4108)  /* dark gray shadow fg     */
#define TUI_COLOR_SHADOW_BG    ((uint16_t)0x0000)  /* black — true shadow bg  */

#define TUI_COLOR_BAR_FILL     ((uint16_t)0xE007)  /* green progress fill     */
#define TUI_COLOR_BAR_EMPTY    ((uint16_t)0x4108)  /* dark empty track        */

/* ── Cell geometry ────────────────────────────────────────────────────────
 *
 * The TUI grid is 8×8 pixel cells.  All coordinates in lcd_tui_* functions
 * are in CELL units unless the name contains _px.
 * Cell (0,0) is the top-left corner of the display.
 */
#define TUI_CELL_W  8
#define TUI_CELL_H  8

/* ── Public API ───────────────────────────────────────────────────────────
 *
 * All x/y coordinates are in CELL units.  w/h are also in cells.
 */

/**
 * @brief Draws the desktop background across the whole screen.
 *
 * Fills every cell with the ░ (light shade) glyph on the desktop colours.
 * Must be called once at boot before any windows are drawn.
 */
void lcd_tui_draw_desktop(void);

/**
 * @brief Draws a filled rectangle of solid colour in cell coordinates.
 *
 * @param cx  Left cell column.
 * @param cy  Top cell row.
 * @param cw  Width in cells.
 * @param ch  Height in cells.
 * @param color  BGR565 fill colour.
 */
void lcd_tui_fill(int cx, int cy, int cw, int ch, uint16_t color);

/**
 * @brief Draws a TUI glyph (box-drawing / block character) at a cell position.
 *
 * @param cx     Cell column.
 * @param cy     Cell row.
 * @param glyph  TUI_GLYPH_* index from lcd_font.h.
 * @param fg     Foreground colour.
 * @param bg     Background colour.
 */
void lcd_tui_draw_glyph(int cx, int cy, int glyph, uint16_t fg, uint16_t bg);

/**
 * @brief Draws an ASCII character at a cell position.
 *
 * Thin wrapper around lcd_draw_char using cell coordinates.
 *
 * @param cx  Cell column.
 * @param cy  Cell row.
 * @param c   ASCII character.
 * @param fg  Foreground colour.
 * @param bg  Background colour.
 */
void lcd_tui_draw_char(int cx, int cy, char c, uint16_t fg, uint16_t bg);

/**
 * @brief Draws a NUL-terminated string starting at a cell position.
 *
 * Clips at the right edge of the display.
 *
 * @param cx   Cell column.
 * @param cy   Cell row.
 * @param str  NUL-terminated ASCII string.
 * @param fg   Foreground colour.
 * @param bg   Background colour.
 */
void lcd_tui_print(int cx, int cy, const char *str, uint16_t fg, uint16_t bg);

/**
 * @brief Draws a string centred within a horizontal cell span.
 *
 * @param cx    Left cell of the span.
 * @param cy    Cell row.
 * @param cw    Width of the span in cells.
 * @param str   NUL-terminated ASCII string (truncated to cw if needed).
 * @param fg    Foreground colour.
 * @param bg    Background colour.
 */
void lcd_tui_print_centered(int cx, int cy, int cw,
                             const char *str, uint16_t fg, uint16_t bg);

/**
 * @brief Draws a double-line bordered window with a drop shadow.
 *
 * The window occupies cells (cx, cy) to (cx+cw-1, cy+ch-1).
 * A 1-cell shadow is drawn 1 cell to the right and 1 cell below.
 * The interior is filled with bg_color.
 *
 * @param cx        Left cell column.
 * @param cy        Top cell row.
 * @param cw        Width in cells (includes border).
 * @param ch        Height in cells (includes border).
 * @param title     Optional title shown centred in the top border, or NULL.
 * @param bg_color  Interior fill colour.
 */
void lcd_tui_draw_window(int cx, int cy, int cw, int ch,
                          const char *title, uint16_t bg_color);

/**
 * @brief Draws a single-line bordered popup with a drop shadow.
 *
 * Lighter visual weight than lcd_tui_draw_window — uses single-line borders
 * and the popup colour scheme.
 *
 * @param cx        Left cell column.
 * @param cy        Top cell row.
 * @param cw        Width in cells (includes border).
 * @param ch        Height in cells (includes border).
 * @param title     Optional title shown centred in the top border, or NULL.
 */
void lcd_tui_draw_popup(int cx, int cy, int cw, int ch, const char *title);

/**
 * @brief Draws or updates a horizontal progress bar inside a window.
 *
 * The bar spans the full interior width of the given cell range.
 * Filled cells use TUI_COLOR_BAR_FILL; empty cells use TUI_COLOR_BAR_EMPTY.
 *
 * @param cx        Left cell of the bar (inside the border).
 * @param cy        Cell row of the bar.
 * @param cw        Width of the bar in cells.
 * @param value     Current value (0 .. max).
 * @param max       Maximum value.
 * @param bg_color  Background colour behind the bar.
 */
void lcd_tui_draw_progress(int cx, int cy, int cw,
                            int value, int max, uint16_t bg_color);
