#include "lcd_log.h"
#include "lcd_tui.h"
#include "lcd_font.h"
#include "lcd_text.h"
#include "config.h"

#include <stdbool.h>
#include <string.h>
#include <stdio.h>

#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"

/* ── Layout ────────────────────────────────────────────────────────────────
 *
 * All measurements in CELLS (8×8 px each).
 *
 * Display variants:
 *   480×320  →  60 cols × 40 rows
 *   320×240  →  40 cols × 30 rows
 *
 * TUI layout:
 *   Full screen = desktop background (░ stipple, drawn once at init)
 *   Main window = double-border, WIN_W × WIN_H cells, shadow bottom-right
 *     Row 0      : ╔═ VIEWOWL v1.x.x ═╗   (title in top border)
 *     Rows 1..2  : header status text
 *     Row 3      : ╠════════════════════╣  (divider)
 *     Rows 4..N  : scrolling log lines
 *   Progress popup: single-border popup drawn over the log area during
 *                   frame transfer, hidden when transfer completes.
 */

#define DCOLS  (LCD_H_RES / TUI_CELL_W)
#define DROWS  (LCD_V_RES / TUI_CELL_H)

/* Main window — centred with margins, leaving room for shadow + desktop ░.
 * 480×320 (60×40 cells): left=2 top=1 right=3(1shadow+2desktop) bottom=3
 * 320×240 (40×30 cells): left=1 top=1 right=2(1shadow+1desktop) bottom=3 */
#define WIN_X   (DCOLS >= 60 ? 2 : 1)
#define WIN_Y   1
#define WIN_W   (DCOLS - WIN_X - (DCOLS >= 60 ? 3 : 2))
#define WIN_H   (DROWS - WIN_Y - 3)

/* Interior (inside double border). */
#define INNER_X  (WIN_X + 1)
#define INNER_Y  (WIN_Y + 1)
#define INNER_W  (WIN_W - 2)

/* Line height: 8px glyph + 5px gap = 13px (~2/3 char height spacing). */
#define LINE_HEIGHT_PX 13

/* Log area starts right below the top border — no header gap, no divider. */
#define LOG_ROW_START  INNER_Y
#define LOG_ROW_END    (WIN_Y + WIN_H - 1)          /* bottom border row      */
#define LOG_AREA_PX    ((LOG_ROW_END - LOG_ROW_START) * TUI_CELL_H)
#define MAX_LOG_LINES  (LOG_AREA_PX / LINE_HEIGHT_PX)

/* Progress popup — centred vertically, sized for line spacing.
 * 6 rows box + 1 row shadow = 7 rows total visible height. */
#define PROG_POPUP_H  6
#define PROG_POPUP_W  (WIN_W - 4)
#define PROG_POPUP_X  (WIN_X + 2)
#define PROG_POPUP_Y  (WIN_Y + (WIN_H - PROG_POPUP_H) / 2)

/* Wait dialog — 8-row popup + 1 shadow = 9 rows total visible height. */
#define WAIT_POPUP_H  8
#define WAIT_POPUP_W  PROG_POPUP_W
#define WAIT_POPUP_X  PROG_POPUP_X
#define WAIT_POPUP_Y  (WIN_Y + (WIN_H - WAIT_POPUP_H) / 2)

#define LOG_COLS  (INNER_W - 1)  /* 1-cell right padding from border */
#define RING_SIZE 48

/* ── Module state ──────────────────────────────────────────────────────── */

typedef struct {
    char            text[LOG_COLS + 1];
    lcd_log_level_t level;
} log_entry_t;

static log_entry_t       s_ring[RING_SIZE];
static int               s_head        = 0;
static int               s_count       = 0;
static bool              s_has_frame   = false;
static bool              s_player_active = false;  /* Type-C: suppress all TUI */
static bool              s_prog_shown  = false;
static bool              s_wait_shown  = false;
static int               s_spinner_idx = 0;
static int               s_spinner_cx  = 0;
static int               s_spinner_cy  = 0;
static SemaphoreHandle_t s_mutex       = NULL;

static const char SPINNER_CHARS[] = { '|', '/', '-', '\\' };

/* ── Colour map ────────────────────────────────────────────────────────── */

static uint16_t level_fg(lcd_log_level_t level)
{
    switch (level) {
        case LCD_LOG_SYSTEM: return TUI_COLOR_WIN_DIM;
        case LCD_LOG_INFO:   return TUI_COLOR_WIN_TEXT;
        case LCD_LOG_WARN:   return TUI_COLOR_POPUP_WARN;
        case LCD_LOG_ERROR:  return TUI_COLOR_POPUP_ERR;
        case LCD_LOG_HINT:   return TUI_COLOR_WIN_DIM;
        default:             return TUI_COLOR_WIN_TEXT;
    }
}

/* ── Internal helpers ──────────────────────────────────────────────────── */

static void redraw_log(void)
{
    lcd_tui_fill(INNER_X, LOG_ROW_START,
                 INNER_W, LOG_ROW_END - LOG_ROW_START,
                 TUI_COLOR_WIN_BG);

    int visible = s_count < MAX_LOG_LINES ? s_count : MAX_LOG_LINES;
    int start   = s_count - visible;
    int base_px = INNER_X * TUI_CELL_W;
    int base_py = LOG_ROW_START * TUI_CELL_H;

    for (int i = 0; i < visible; i++) {
        int ring_idx = (s_head + start + i) % RING_SIZE;
        const log_entry_t *e = &s_ring[ring_idx];
        int py = base_py + i * LINE_HEIGHT_PX;
        lcd_draw_string(base_px, py, e->text,
                        level_fg(e->level), TUI_COLOR_WIN_BG);
    }
}

/* ── Public API ────────────────────────────────────────────────────────── */

void lcd_log_init(const char *title)
{
    if (!s_mutex) {
        s_mutex = xSemaphoreCreateMutex();
    }

    s_head          = 0;
    s_count         = 0;
    s_has_frame     = false;
    s_player_active = false;
    s_prog_shown    = false;
    s_wait_shown    = false;

    /* Desktop stipple background. */
    lcd_tui_draw_desktop();

    /* Main window with title, double border, shadow. */
    lcd_tui_draw_window(WIN_X, WIN_Y, WIN_W, WIN_H, title, TUI_COLOR_WIN_BG);

    /* Empty log area. */
    redraw_log();
}

void lcd_log_set_has_frame(bool has_frame)
{
    if (s_mutex) xSemaphoreTake(s_mutex, portMAX_DELAY);
    s_has_frame = has_frame;
    if (has_frame) {
        /* Frame render painted the full LCD — reset all popup state so the
         * next transfer cycle starts with a clean slate. */
        s_prog_shown = false;
        s_wait_shown = false;
    }
    if (s_mutex) xSemaphoreGive(s_mutex);
}

void lcd_log_print(lcd_log_level_t level, const char *message)
{
    if (!message) return;

    /* Auto-prepend level prefix: [SYS] [INF] [WRN] [ERR], hints untagged. */
    static const char * const prefixes[] = {
        "[SYS] ", "[INF] ", "[WRN] ", "[ERR] ", ""
    };
    const char *pfx = (level <= LCD_LOG_HINT) ? prefixes[level] : "";
    char buf[LOG_COLS + 1];
    snprintf(buf, sizeof(buf), "%s%s", pfx, message);

    if (s_mutex) xSemaphoreTake(s_mutex, portMAX_DELAY);

    if (s_player_active || s_has_frame) {
        /* Player active (Type C) or a frame is on screen (Type A/B):
         * suppress all text output so the displayed image is not corrupted.
         * The dedicated lcd_log_show_no_server() function provides the only
         * permitted overlay for Type A/B after a 30-second server timeout. */
        if (s_mutex) xSemaphoreGive(s_mutex);
        return;
    }

    /* Append to ring buffer. */
    int write_idx;
    if (s_count < RING_SIZE) {
        write_idx = (s_head + s_count) % RING_SIZE;
        s_count++;
    } else {
        write_idx = s_head;
        s_head    = (s_head + 1) % RING_SIZE;
    }

    log_entry_t *entry = &s_ring[write_idx];
    entry->level = level;
    strncpy(entry->text, buf, LOG_COLS);
    entry->text[LOG_COLS] = '\0';

    redraw_log();

    if (s_mutex) xSemaphoreGive(s_mutex);
}

void lcd_log_show_progress(const char *label, int value, int max)
{
    if (s_has_frame || s_player_active) return;   /* No UX when a frame is on screen or player is active. */
    if (s_mutex) xSemaphoreTake(s_mutex, portMAX_DELAY);

    if (!s_prog_shown) {
        lcd_tui_draw_popup(PROG_POPUP_X, PROG_POPUP_Y,
                           PROG_POPUP_W, PROG_POPUP_H,
                           "LOADING FRAME");
        s_prog_shown = true;
    }

    int popup_py = PROG_POPUP_Y * TUI_CELL_H;
    int bar_x    = PROG_POPUP_X + 1;
    int bar_w    = PROG_POPUP_W - 2;

    /* Label: 6px below top border of popup. */
    if (label) {
        int label_py = popup_py + TUI_CELL_H + 2;
        lcd_fill_rect(bar_x * TUI_CELL_W, label_py,
                      bar_w * TUI_CELL_W, TUI_CELL_H, TUI_COLOR_POPUP_BG);
        lcd_tui_print_centered(bar_x, PROG_POPUP_Y + 1, bar_w,
                               label, TUI_COLOR_POPUP_TEXT, TUI_COLOR_POPUP_BG);
    }

    /* Bar: label + LINE_HEIGHT_PX below. */
    int bar_cell_row = PROG_POPUP_Y + 3;
    lcd_tui_draw_progress(bar_x, bar_cell_row, bar_w,
                          value, max, TUI_COLOR_POPUP_BG);

    if (s_mutex) xSemaphoreGive(s_mutex);
}

void lcd_log_hide_progress(void)
{
    if (!s_prog_shown) return;
    if (s_mutex) xSemaphoreTake(s_mutex, portMAX_DELAY);

    /* Erase popup area (+1 for shadow) and restore log. */
    lcd_tui_fill(PROG_POPUP_X, PROG_POPUP_Y,
                 PROG_POPUP_W + 1, PROG_POPUP_H + 1,
                 TUI_COLOR_WIN_BG);
    s_prog_shown = false;
    redraw_log();

    if (s_mutex) xSemaphoreGive(s_mutex);
}

void lcd_log_show_wait(const char *title, const char *line1, const char *line2)
{
    if (s_has_frame || s_player_active) return;   /* No UX when a frame is on screen or player is active. */
    if (s_mutex) xSemaphoreTake(s_mutex, portMAX_DELAY);

    lcd_tui_draw_popup(WAIT_POPUP_X, WAIT_POPUP_Y,
                       WAIT_POPUP_W, WAIT_POPUP_H,
                       title);
    s_wait_shown  = true;
    s_spinner_idx = 0;

    int ix  = WAIT_POPUP_X + 1;
    int iw  = WAIT_POPUP_W - 2;
    int ppx = ix * TUI_CELL_W;
    int ppy = WAIT_POPUP_Y * TUI_CELL_H;

    /* Line 1: 10px below popup top (border + 2px pad). */
    int l1_py = ppy + TUI_CELL_H + 2;
    lcd_draw_string(ppx + TUI_CELL_W, l1_py, line1,
                    TUI_COLOR_POPUP_TEXT, TUI_COLOR_POPUP_BG);

    /* Line 2: LINE_HEIGHT_PX below line 1. */
    int l2_py = l1_py + LINE_HEIGHT_PX;
    lcd_draw_string(ppx + TUI_CELL_W, l2_py, line2,
                    TUI_COLOR_POPUP_TEXT, TUI_COLOR_POPUP_BG);

    /* Spinner: LINE_HEIGHT_PX × 2 below line 2 (extra gap). */
    int spin_py  = l2_py + LINE_HEIGHT_PX * 2;
    int wlen     = 10;                       /* strlen("Waiting...") */
    int pad      = (iw - wlen - 3) / 2;
    lcd_draw_string(ppx + pad * TUI_CELL_W, spin_py, "Waiting...",
                    TUI_COLOR_POPUP_TEXT, TUI_COLOR_POPUP_BG);
    /* Save spinner char pixel position for lcd_log_spin. */
    s_spinner_cx = ppx + (pad + wlen + 2) * TUI_CELL_W;
    s_spinner_cy = spin_py;

    if (s_mutex) xSemaphoreGive(s_mutex);
}

void lcd_log_spin(void)
{
    if (!s_wait_shown || s_player_active) return;
    if (s_mutex) xSemaphoreTake(s_mutex, portMAX_DELAY);

    s_spinner_idx = (s_spinner_idx + 1) % 4;
    lcd_draw_char(s_spinner_cx, s_spinner_cy,
                  SPINNER_CHARS[s_spinner_idx],
                  TUI_COLOR_POPUP_TEXT, TUI_COLOR_POPUP_BG);

    if (s_mutex) xSemaphoreGive(s_mutex);
}

void lcd_log_hide_wait(void)
{
    if (!s_wait_shown) return;
    if (s_mutex) xSemaphoreTake(s_mutex, portMAX_DELAY);

    lcd_tui_fill(WAIT_POPUP_X, WAIT_POPUP_Y,
                 WAIT_POPUP_W + 1, WAIT_POPUP_H + 1,
                 TUI_COLOR_WIN_BG);
    s_wait_shown = false;
    redraw_log();

    if (s_mutex) xSemaphoreGive(s_mutex);
}

void lcd_log_set_player_active(bool active)
{
    if (s_mutex) xSemaphoreTake(s_mutex, portMAX_DELAY);
    s_player_active = active;
    if (!active) {
        /* Restore clean popup state for the upcoming batch download. */
        s_prog_shown = false;
        s_wait_shown = false;
    }
    if (s_mutex) xSemaphoreGive(s_mutex);
}

void lcd_log_show_no_server(void)
{
    /* Only valid for Type A/B: a frame is on screen but player is not active. */
    if (s_player_active || !s_has_frame) return;

    /* Paint a dim single-line hint over the last 8 pixel rows of the display.
     * Deliberately avoids using the TUI system (no cell-based coordinates)
     * so it never triggers a TUI redraw over the frame content. */
    int y = LCD_V_RES - TUI_CELL_H;
    lcd_fill_rect(0, y, LCD_H_RES, TUI_CELL_H, 0x0000u);
    lcd_draw_string(4, y, "* NO SERVER",
                    TUI_COLOR_WIN_DIM, 0x0000u);
}
