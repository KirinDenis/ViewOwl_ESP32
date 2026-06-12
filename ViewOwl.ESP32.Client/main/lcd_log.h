#pragma once
#include <stdbool.h>
#include <stdint.h>

/**
 * @brief Log level used to choose the line foreground color.
 */
typedef enum {
    LCD_LOG_SYSTEM = 0, /* cyan  — boot stage / hardware events */
    LCD_LOG_INFO   = 1, /* green — successful operations        */
    LCD_LOG_WARN   = 2, /* yellow — warnings / retries          */
    LCD_LOG_ERROR  = 3, /* red   — failures                     */
    LCD_LOG_HINT   = 4, /* dim white — tips shown after errors  */
} lcd_log_level_t;

/**
 * @brief Initialises the on-screen log.
 *
 * Clears the display, draws the title bar, and resets the line counter.
 * Must be called after lcd_init() and before any lcd_log_* calls.
 *
 * @param title Short title string shown in the header (e.g. "VIEWOWL v1.0.0").
 */
void lcd_log_init(const char *title);

/**
 * @brief Appends a message to the on-screen log.
 *
 * When the log area is full the oldest visible lines are scrolled off and
 * the display is redrawn from the internal ring buffer.
 * When overlay mode is active (see lcd_log_set_has_frame) the message is
 * drawn as a single status line at the very bottom of the screen instead
 * so the displayed image is not overwritten.
 *
 * @param level   Log level controlling the text color.
 * @param message NUL-terminated ASCII message (truncated to fit one line).
 */
void lcd_log_print(lcd_log_level_t level, const char *message);

/**
 * @brief Switches between full-screen log mode and single-line overlay mode.
 *
 * Call with @p has_frame = true after a frame has been successfully rendered
 * to the LCD.  In overlay mode lcd_log_print draws a single status line at
 * the bottom of the screen (last LINE_H pixels) rather than scrolling the
 * log over the image.  Call with false on boot or after lcd_log_init to
 * restore normal scrolling log behaviour.
 *
 * @param has_frame true  — overlay mode (image on screen, keep it visible).
 *                  false — normal scrolling log mode.
 */
void lcd_log_set_has_frame(bool has_frame);

/**
 * @brief Shows or updates the frame-loading progress popup.
 *
 * Draws a single-border popup with a progress bar over the log area.
 * Safe to call repeatedly — redraws only the bar and label on subsequent
 * calls without recreating the popup border.
 *
 * @param label  Short status string shown above the bar, or NULL.
 * @param value  Current progress value (0 .. max).
 * @param max    Maximum value.
 */
void lcd_log_show_progress(const char *label, int value, int max);

/**
 * @brief Hides the progress popup and restores the log area.
 *
 * No-op when the popup is not currently visible.
 */
void lcd_log_hide_progress(void);

/**
 * @brief Shows a Turbo Vision wait dialog with a spinning indicator.
 *
 * Draws a popup with two text lines and a "Waiting... |" spinner row.
 * Call lcd_log_spin() periodically (~200 ms) to animate the spinner.
 * Safe to call repeatedly — redraws the popup each time.
 *
 * @param title  Popup title shown in the top border.
 * @param line1  First text line.
 * @param line2  Second text line.
 */
void lcd_log_show_wait(const char *title, const char *line1, const char *line2);

/**
 * @brief Advances the spinner animation in the wait dialog.
 *
 * Cycles through | / - \\ characters. No-op when no wait dialog is visible.
 */
void lcd_log_spin(void);

/**
 * @brief Hides the wait dialog and restores the log area.
 *
 * No-op when no wait dialog is visible.
 */
void lcd_log_hide_wait(void);

/**
 * @brief Activates or deactivates player-active mode (Type-C Sci-Fi templates).
 *
 * When @p active is true every lcd_log_* call becomes a no-op so that the
 * frame player can render without any TUI overlays interrupting the image.
 * Call with false before starting a batch download so progress indicators
 * are restored.
 *
 * @param active true — player running, suppress all TUI output.
 *               false — normal TUI operation.
 */
void lcd_log_set_player_active(bool active);

/**
 * @brief Shows a non-destructive "NO SERVER" indicator at the bottom of the LCD.
 *
 * Intended for Type-A/B frames: after 30 seconds without a server response
 * a dim one-line hint is painted over the last 8 pixel rows of the display.
 * No-op when the player is active or no frame has been rendered yet.
 */
void lcd_log_show_no_server(void);

/* Convenience wrappers */
#define lcd_log_system(msg) lcd_log_print(LCD_LOG_SYSTEM, (msg))
#define lcd_log_info(msg)   lcd_log_print(LCD_LOG_INFO,   (msg))
#define lcd_log_warn(msg)   lcd_log_print(LCD_LOG_WARN,   (msg))
#define lcd_log_error(msg)  lcd_log_print(LCD_LOG_ERROR,  (msg))
#define lcd_log_hint(msg)   lcd_log_print(LCD_LOG_HINT,   (msg))
