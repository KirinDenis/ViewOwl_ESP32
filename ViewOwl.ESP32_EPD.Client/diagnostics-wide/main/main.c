/**
 * @file main.c
 * @brief WIDE 792x272 4-GRAY probe v4 — VERBATIM Waveshare port (EPD_5in79.c).
 *
 * Probes v1-v3 (4.2" LUT + our own cascade geometry) proved the glass CAN show
 * four tones (master half rendered them) but scan geometry broke and the slave
 * half was mud. v4 abandons our geometry entirely and ports Waveshare's PROVEN
 * 4-gray driver for this exact dual-SSD1683 792x272 glass, command for command:
 * their LUT, their init (booster 0xA6 tail, border 0x81, row-major data entry
 * 0x11=0x01 / slave 0x91=0x00, both windows set once), their four-plane stream
 * order with no cursor rewrites, their 0xCF activation.
 *
 * ONE screen: 8 tone bars (BLACK/DARK/LIGHT/WHITE per half) + frame + two
 * orientation markers (solid block near the logical TOP-LEFT, single tick above
 * the first bar). Waveshare's addressing may land our logical image flipped
 * relative to the mono driver's mapping — the markers tell us exactly which
 * flip (if any) to bake into the final client driver.
 *
 * Report: (1) 4 distinct tones on EACH half? (2) full height used?
 * (3) where is the block — top-left / top-right / bottom-left / bottom-right?
 */
#include "epd_wide.h"

#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include <string.h>

static const char *TAG = "epd_wide_diag";

/* Linear 2bpp logical image, 792x272, 4 px/byte (53856 B) — static, not stack. */
static uint8_t s_img[EPD_G2_BUF_SIZE];

static void g2_rect(int x0, int y0, int x1, int y1, uint8_t code)
{
    for (int y = y0; y <= y1; y++) {
        for (int x = x0; x <= x1; x++) {
            epd_g2_set_pixel(s_img, x, y, code);
        }
    }
}

void app_main(void)
{
    ESP_LOGI(TAG, "=== WIDE 792x272 4-GRAY probe v4 (verbatim Waveshare port) ===");

    epd_gpio_init();

    /* Known-good mono clear first — clean white baseline, no ghosting. */
    epd_init();
    epd_clear();

    /* All white (0xFF = four 2-bit WHITE codes per byte). */
    memset(s_img, 0xFF, sizeof(s_img));

    /* 8 tone bars: B,D,L,W on each half. */
    static const uint8_t seq[4] = { EPD_G_BLACK, EPD_G_DARK, EPD_G_LIGHT, EPD_G_WHITE };
    for (int b = 0; b < 8; b++) {
        int x0 = b * 99;
        int x1 = (b == 7) ? (EPD_LOGICAL_W - 1) : (x0 + 98);
        g2_rect(x0, 40, x1, EPD_LOGICAL_H - 41, seq[b & 3]);
    }

    /* Thin black frame. */
    g2_rect(0, 0, EPD_LOGICAL_W - 1, 3, EPD_G_BLACK);
    g2_rect(0, EPD_LOGICAL_H - 4, EPD_LOGICAL_W - 1, EPD_LOGICAL_H - 1, EPD_G_BLACK);
    g2_rect(0, 0, 3, EPD_LOGICAL_H - 1, EPD_G_BLACK);
    g2_rect(EPD_LOGICAL_W - 4, 0, EPD_LOGICAL_W - 1, EPD_LOGICAL_H - 1, EPD_G_BLACK);

    /* Orientation markers: solid block near the logical TOP-LEFT corner and a
     * single tick right of it. Their physical position reveals any flip. */
    g2_rect(14, 10, 44, 32, EPD_G_BLACK);
    g2_rect(56, 10, 68, 32, EPD_G_DARK);

    epd_init_4gray_ws();
    epd_display_4gray_ws(s_img);

    epd_sleep();
    ESP_LOGI(TAG, "=== done - report: tones per half / full height? / block corner ===");
}
