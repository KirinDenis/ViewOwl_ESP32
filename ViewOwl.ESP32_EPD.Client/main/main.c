/**
 * @file main.c
 * @brief ViewOwl ESP32-S3 e-paper client - entry point.
 *
 * M2a: bring up the 4.2" e-paper, draw a boot screen, then connect WiFi
 *      (credentials from NVS, falling back to config.h). Status goes to serial -
 *      e-ink refresh is too slow for a live status indicator, so the boot screen
 *      is drawn once. UDP HELLO/AUTH + frame rendering follow in M2c / M3.
 */
#include <string.h>
#include <stdio.h>
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_log.h"
#include "esp_wifi.h"
#include "esp_system.h"

#include "config.h"
#include "epd_4in2.h"
#include "epd_text.h"
#include "provision.h"
#include "nvs_storage.h"   /* shared from the classic client */
#include "wifi_init.h"     /* shared from the classic client */
#include "udp_client.h"

static const char *TAG = "main";

/* 1-bit boot-screen framebuffer (15 KB) - static, never on a task stack. */
static uint8_t s_buf[EPD_BUF_SIZE];

/* Boot self-tests (experiments). */
#define EPD_PARTIAL_DEMO      0   /* 1-bit partial sweep — proven 2026-06-23 */
#define EPD_GRAY_PARTIAL_DEMO 0   /* 4-gray windowed partial — experiment done 2026-06-23 */

static void px(int x, int y, int black)
{
    if (x < 0 || x >= EPD_W || y < 0 || y >= EPD_H) {
        return;
    }
    int idx = y * EPD_ROW_BYTES + (x >> 3);
    uint8_t bit = 0x80 >> (x & 7);
    if (black) {
        s_buf[idx] &= ~bit;
    } else {
        s_buf[idx] |= bit;
    }
}

static void fill_rect(int x0, int y0, int x1, int y1, int black)
{
    for (int y = y0; y <= y1; y++) {
        for (int x = x0; x <= x1; x++) {
            px(x, y, black);
        }
    }
}

#define STATUS_Y 150   /* baseline (top) of the live status value text */
#define STATUS_X 96    /* x where the status value starts (after "STATUS:") */

/** @brief Boot screen: framed, an inverted title bar (white on black), build info,
 *  and a live status line. Drawn directly black-on-white (no inversion). */
static void epd_boot_screen(void)
{
    memset(s_buf, EPD_WHITE, sizeof(s_buf));
    /* 4 px frame */
    fill_rect(0, 0, EPD_W - 1, 3, 1);
    fill_rect(0, EPD_H - 4, EPD_W - 1, EPD_H - 1, 1);
    fill_rect(0, 0, 3, EPD_H - 1, 1);
    fill_rect(EPD_W - 4, 0, EPD_W - 1, EPD_H - 1, 1);

    /* inverted title bar — white text on a solid black bar */
    fill_rect(12, 22, EPD_W - 13, 50, 1);
    epd_draw_text(s_buf, 140, 33, "VIEWOWL E-PAPER", 0);

    /* build info — black text on white */
    epd_draw_text(s_buf, 24, 78, FIRMWARE_VERSION, 1);
    epd_draw_text(s_buf, 24, 96, "4.2\" 400x300 SSD1683", 1);

    /* status line */
    epd_draw_text(s_buf, 24, STATUS_Y, "STATUS:", 1);
    epd_draw_text(s_buf, STATUS_X, STATUS_Y, "booting", 1);

    epd_init();
    epd_display(s_buf);
}

/** @brief Replace the status value via a fast partial refresh (only that strip flickers). */
static void epd_status(const char *msg)
{
    fill_rect(STATUS_X, STATUS_Y, EPD_W - 13, STATUS_Y + 7, 0);   /* erase the value */
    epd_draw_text(s_buf, STATUS_X, STATUS_Y, msg, 1);
    epd_display_part(0, STATUS_Y - 2, EPD_W, 22, s_buf + (STATUS_Y - 2) * EPD_ROW_BYTES);
}

#if EPD_PARTIAL_DEMO
/**
 * @brief Sweep a black block across a horizontal strip using partial refresh.
 *
 * Baseline is the boot screen (just full-refreshed). Each step rewrites only the
 * strip's RAM window and triggers the fast partial waveform, so only the strip
 * flickers - the rest of the boot screen stays put. Proves partial refresh works.
 */
static void epd_partial_demo(void)
{
    static uint8_t strip[EPD_ROW_BYTES * 30]; /* full-width strip, 30 px tall */
    const uint16_t ry = 230, rh = 30;
    const int steps = 14, bw = 48;            /* block width in px */

    for (int s = 0; s < steps; s++) {
        memset(strip, EPD_WHITE, sizeof(strip));
        int bx = (EPD_W - bw) * s / (steps - 1);
        for (int yy = 0; yy < rh; yy++) {
            for (int px = bx; px < bx + bw; px++) {
                strip[yy * EPD_ROW_BYTES + (px >> 3)] &= ~(0x80 >> (px & 7)); /* black */
            }
        }
        epd_display_part(0, ry, EPD_W, rh, strip);
        vTaskDelay(pdMS_TO_TICKS(120));
    }
}
#endif

#if EPD_GRAY_PARTIAL_DEMO
static uint8_t s_g4[EPD_BUF_SIZE_4GRAY]; /* 30 KB 2-bit baseline framebuffer */
static uint8_t s_g4r[100 * 60];          /* region buffer: 400 px x 60 rows, 2-bit */

static void g4_set(uint8_t *buf, int stride, int x, int y, int code)
{
    uint8_t *p = &buf[y * stride + (x >> 2)];
    int sh = 6 - (x & 3) * 2;
    *p = (uint8_t)((*p & ~(0x03 << sh)) | ((code & 3) << sh));
}

/* 4-gray partial experiment: full 4-gray baseline (4 vertical grey bands), then a
 * windowed 4-gray partial that paints a horizontal strip with the bands REVERSED.
 * Watch: does only the strip update (windowed), or does the whole panel flash?
 * Do the strip edges ghost? */
static void epd_gray_partial_demo(void)
{
    const int stride = EPD_ROW_BYTES * 2; /* 100 bytes/row (2-bit) */
    for (int y = 0; y < EPD_H; y++) {
        for (int x = 0; x < EPD_W; x++) {
            g4_set(s_g4, stride, x, y, x / 100); /* bands: 0 1 2 3 across 400 px */
        }
    }
    epd_init_4gray();
    epd_display_4gray(s_g4);
    ESP_LOGI(TAG, "4-gray baseline shown; windowed partial in 3 s");
    vTaskDelay(pdMS_TO_TICKS(3000));

    const int rstride = (EPD_W + 3) / 4; /* 100 */
    for (int y = 0; y < 60; y++) {
        for (int x = 0; x < EPD_W; x++) {
            g4_set(s_g4r, rstride, x, y, 3 - (x / 100)); /* reversed bands */
        }
    }
    epd_display_part_4gray(0, 120, EPD_W, 60, s_g4r);
}
#endif

void app_main(void)
{
    ESP_LOGI(TAG, "=== ViewOwl EPD client %s ===", FIRMWARE_VERSION);
    ESP_LOGI(TAG, "Display: 4.2\" e-paper %dx%d (SSD1683)", EPD_W, EPD_H);

    epd_gpio_init();
    epd_boot_screen();
#if EPD_PARTIAL_DEMO
    ESP_LOGI(TAG, "partial-refresh demo: sweeping a block across a strip");
    epd_partial_demo();
#endif
#if EPD_GRAY_PARTIAL_DEMO
    ESP_LOGI(TAG, "4-gray partial experiment: baseline bands + windowed partial strip");
    epd_gray_partial_demo();
#endif

    ESP_ERROR_CHECK(nvs_storage_init());

    /* Provisioning: if the token or WiFi creds are missing from NVS, wait for the
     * browser (BURN wizard) to send them over USB serial. On success reboot with
     * everything stored; on timeout fall through to the config.h defaults. The
     * e-paper is too slow for a live indicator, so a one-shot status line is shown. */
    if (!nvs_storage_has_token() || !nvs_storage_has_wifi()) {
        ESP_LOGW(TAG, "No credentials in NVS - entering provisioning (USB serial)");
        epd_status("provisioning...");
        if (provision_wait(120000)) {
            ESP_LOGI(TAG, "Provisioned - rebooting");
            epd_status("provisioned, reboot");
            vTaskDelay(pdMS_TO_TICKS(500));
            esp_restart();
        }
        ESP_LOGW(TAG, "Provisioning timed out - using config.h defaults");
    }

    /* Log the device token so it can be registered on the server (HELLO gate). */
    {
        char tok[NVS_TOKEN_HEX_LEN + 1] = {0};
        if (nvs_storage_read_token(tok, sizeof(tok)) == ESP_OK) {
            ESP_LOGW(TAG, "DEVICE TOKEN (register on server): %s", tok);
        } else {
            ESP_LOGW(TAG, "No token in NVS - using config.h TOKEN");
        }
    }

    /* Resolve WiFi credentials: prefer NVS (provisioned), fall back to config.h. */
    char ssid[NVS_SSID_MAX_LEN + 1] = WIFI_SSID;
    char pass[NVS_PASS_MAX_LEN + 1] = WIFI_PASS;
    if (nvs_storage_read_ssid(ssid, sizeof(ssid)) != ESP_OK) {
        ESP_LOGW(TAG, "NVS SSID missing - using config.h default");
    }
    if (nvs_storage_read_pass(pass, sizeof(pass)) != ESP_OK) {
        ESP_LOGW(TAG, "NVS password missing - using config.h default");
    }

    ESP_LOGI(TAG, "Connecting: SSID='%s' (%d chars), password length %d",
             ssid, (int)strlen(ssid), (int)strlen(pass));

    epd_status("WiFi connecting...");
    wifi_init_sta(ssid, pass);

    wifi_ap_record_t ap;
    bool connected = (esp_wifi_sta_get_ap_info(&ap) == ESP_OK);
    if (!connected) {
        ESP_LOGE(TAG, "Wi-Fi FAILED - not connected (set SSID/password in config.h or provision)");
        epd_status("WiFi FAILED");
        for (;;) {
            vTaskDelay(pdMS_TO_TICKS(5000));
            ESP_LOGW(TAG, "Still offline - check credentials");
        }
    }

    ESP_LOGI(TAG, "Wi-Fi connected: '%s' (rssi %d)", ssid, ap.rssi);

    {
        char st[40];
        snprintf(st, sizeof(st), "WiFi OK  rssi %d", ap.rssi);
        epd_status(st);
    }

    /* Class-C frame player (idles until a batch is committed). */
    frame_player_init();

    epd_status("server linking...");

    /* Hand off to the UDP client - HELLO -> AUTH -> DATA/BATCH -> PING loop. */
    xTaskCreate(udp_client_task, "udp_client", 6144, NULL, 5, NULL);

    for (;;) {
        vTaskDelay(pdMS_TO_TICKS(5000));
    }
}
