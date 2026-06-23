/**
 * @file main.c
 * @brief ViewOwl ESP32-C3 client - entry point.
 *
 * M1: bring up the round GC9A01 display + boot screen.
 * M2a: WiFi station - resolve credentials (NVS -> config.h fallback), connect,
 *      show status on the round display (cyan boot -> yellow connecting -> green).
 *      Serial provisioning is M2b (the classic serial_provision is LCD-coupled
 *      and reads UART0, which is not the C3 console - needs adapting).
 */

#include <string.h>
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_log.h"
#include "esp_wifi.h"
#include "esp_system.h"

#include "config.h"
#include "lcd_gc9a01.h"
#include "provision.h"
#include "udp_client.h"
#include "nvs_storage.h"   /* shared from the classic client */
#include "wifi_init.h"     /* shared from the classic client */

static const char *TAG = "main";

/* RGB565 - render correctly thanks to GC9A01 init (BGR + INVON). */
#define COL_BG      0x0008   /* near-black navy */
#define COL_CYAN    0x07FF
#define COL_YELLOW  0xFFE0
#define COL_GREEN   0x07E0
#define COL_RED     0xF800
#define COL_ORANGE  0xFD20   /* WiFi ok but AUTH rejected */
#define COL_MAGENTA 0xF81F   /* provisioning - waiting for browser */
#define COL_CENTRE  0xFFFF

/* Draw the status ring at the safe radius (112) so the bezel tolerance can't clip it. */
static void status_ring(uint16_t colour)
{
    const int cx = LCD_H_RES / 2;
    const int cy = LCD_V_RES / 2;
    lcd_gc9a01_fill(COL_BG);
    lcd_gc9a01_fill_circle(cx, cy, 112, colour);
    lcd_gc9a01_fill_circle(cx, cy, 104, COL_BG);     /* punch hole -> ring */
    lcd_gc9a01_fill_circle(cx, cy, 18,  COL_CENTRE);
}

void app_main(void)
{
    ESP_LOGI(TAG, "=== ViewOwl C3 client %s ===", FIRMWARE_VERSION);
    ESP_LOGI(TAG, "Display: GC9A01 %dx%d round (CrowPanel 1.28\")", LCD_H_RES, LCD_V_RES);

    lcd_gc9a01_init();
    status_ring(COL_CYAN);                 /* boot */

    ESP_ERROR_CHECK(nvs_storage_init());

    /* Provisioning: if the token or WiFi creds are missing from NVS, wait for the
     * browser (BURN wizard) to send them over USB serial. On success reboot with
     * everything stored; on timeout fall through to the config.h defaults. */
    if (!nvs_storage_has_token() || !nvs_storage_has_wifi()) {
        ESP_LOGW(TAG, "No credentials in NVS - entering provisioning (USB serial)");
        status_ring(COL_MAGENTA);          /* waiting for the browser */
        if (provision_wait(120000)) {
            ESP_LOGI(TAG, "Provisioned - rebooting");
            status_ring(COL_GREEN);
            vTaskDelay(pdMS_TO_TICKS(500));
            esp_restart();
        }
        ESP_LOGW(TAG, "Provisioning timed out - using config.h defaults");
    }

    /* Log the device token so it can be registered on the server (M3 gate):
     * the UDP server rejects HELLO from an unregistered token. */
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

    /* Safe diagnostic: SSID + password LENGTH (not the password) - a length that
     * differs from what you typed means the credential was truncated in transit. */
    ESP_LOGI(TAG, "Connecting: SSID='%s' (%d chars), password length %d",
             ssid, (int)strlen(ssid), (int)strlen(pass));
    status_ring(COL_YELLOW);               /* connecting */

    /* wifi_init_sta returns on EITHER success OR exhausted retries - it does not
     * report which. Query the real state so the ring tells the truth. */
    wifi_init_sta(ssid, pass);

    wifi_ap_record_t ap;
    bool connected = (esp_wifi_sta_get_ap_info(&ap) == ESP_OK);

    if (!connected) {
        ESP_LOGE(TAG, "Wi-Fi FAILED - not connected (check SSID/password in config.h)");
        status_ring(COL_RED);              /* honestly red, not a fake green */
        for (;;) {
            vTaskDelay(pdMS_TO_TICKS(5000));
            ESP_LOGW(TAG, "Still offline - reflash with correct credentials");
        }
    }

    ESP_LOGI(TAG, "Wi-Fi connected: '%s' (rssi %d)", ssid, ap.rssi);
    status_ring(COL_GREEN);

    /* M3b: hand off to the frame client. It loops HELLO -> AUTH -> DATA -> DONE,
     * receiving one frame per cycle. M3b-1 logs each frame's size + format flag
     * over serial; M3b-2 will decode and blit it to the round display.
     * Static buffers inside the task keep its stack small; 6 KB is ample. */
    xTaskCreate(udp_client_task, "udp_client", 6144, NULL, 5, NULL);

    /* The client task runs independently; keep app_main alive. */
    for (;;) {
        vTaskDelay(pdMS_TO_TICKS(5000));
    }
}
