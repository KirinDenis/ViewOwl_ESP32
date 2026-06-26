/**
 * @file main.c
 * @brief 4-gray ("mono CGA") test — shows the 4 native grey levels.
 *
 * Draws four vertical bands across the panel: white | light-grey | dark-grey | black.
 * This proves the SSD1683 4-gray mode (custom LUT + 2-bit-per-pixel dual-plane encoding)
 * works on our panel — the basis for the "mono CGA" render profile.
 */
#include "epd_4in2.h"

#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include <string.h>

static const char *TAG = "epd_diag";

/* 2-bit framebuffer (30 KB) — static so it never sits on a task stack. */
static uint8_t s_buf[EPD_BUF_SIZE_4GRAY];

/** @brief Set one pixel to a grey level: 0 = black, 1 = dark, 2 = light, 3 = white. */
static void px4(int x, int y, int level)
{
    if (x < 0 || x >= EPD_W || y < 0 || y >= EPD_H) {
        return;
    }
    int i = x >> 3;
    int p = x & 7;
    int j = p >> 2;                 /* which of the group's 2 bytes */
    int shift = 6 - 2 * (p & 3);    /* MSB-first 2-bit slot in the byte */
    int idx = (y * EPD_ROW_BYTES + i) * 2 + j;
    s_buf[idx] = (s_buf[idx] & ~(0x03 << shift)) | ((level & 0x03) << shift);
}

static void fill4(int x0, int y0, int x1, int y1, int level)
{
    for (int y = y0; y <= y1; y++) {
        for (int x = x0; x <= x1; x++) {
            px4(x, y, level);
        }
    }
}

void app_main(void)
{
    ESP_LOGI(TAG, "=== EPD 4-gray (mono CGA) test ===");

    epd_gpio_init();
    epd_init_4gray();

    memset(s_buf, 0xFF, sizeof(s_buf));     /* all white (code 0b11) */

    /* Four vertical bands, 100 px each: white | light | dark | black. */
    fill4(0,   0,  99, EPD_H - 1, 3);
    fill4(100, 0, 199, EPD_H - 1, 2);
    fill4(200, 0, 299, EPD_H - 1, 1);
    fill4(300, 0, 399, EPD_H - 1, 0);

    epd_display_4gray(s_buf);
    epd_sleep();
    ESP_LOGI(TAG, "=== done — expect 4 shades L->R: white, light, dark, black ===");
}
