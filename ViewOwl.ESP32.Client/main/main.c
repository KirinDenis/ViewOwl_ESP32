#include "lcd_init.h"
#include "lcd_log.h"
#include "wifi_init.h"
#include "udp_client.h"
#include "nvs_storage.h"
#include "serial_provision.h"
#include "config.h"

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_task_wdt.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "esp_system.h"

static const char *TAG = "main";

static int      s_maincount = 0;
extern int64_t  last_success_for_WDT;

/* Human-readable reset reason for boot diagnostics. */
static const char *reset_reason_str(esp_reset_reason_t r)
{
    switch (r) {
        case ESP_RST_POWERON:  return "POWER_ON";
        case ESP_RST_SW:       return "SW_RESET (esp_restart)";
        case ESP_RST_PANIC:    return "PANIC (guru meditation)";
        case ESP_RST_INT_WDT:  return "INT_WDT (interrupt watchdog)";
        case ESP_RST_TASK_WDT: return "TASK_WDT (task watchdog)";
        case ESP_RST_WDT:      return "WDT (other watchdog)";
        case ESP_RST_DEEPSLEEP: return "DEEP_SLEEP";
        case ESP_RST_BROWNOUT: return "BROWNOUT";
        default:               return "UNKNOWN";
    }
}

void app_main(void)
{
    esp_reset_reason_t reason = esp_reset_reason();
    ESP_LOGW("main", "=== BOOT: %s (code %d) ===", reset_reason_str(reason), (int)reason);

    /* WDT: deinit the default config then re-init with a 5-second timeout. */
    esp_task_wdt_deinit();
    const esp_task_wdt_config_t twdt_config = {
        .timeout_ms     = 5000,
        .idle_core_mask = (1 << portNUM_PROCESSORS) - 1,
        .trigger_panic  = true,
    };
    ESP_ERROR_CHECK(esp_task_wdt_init(&twdt_config));
    ESP_ERROR_CHECK(esp_task_wdt_add(NULL));

    /* NVS must be ready before any provisioning check. */
    ESP_ERROR_CHECK(nvs_storage_init());

    /* LCD must be ready before the log can draw anything. */
    lcd_init();

    /* Boot log: title bar + first system messages.
     * lcd_log_init() draws the full TUI desktop (~2400 cells × 65 SPI ops each)
     * which can take several seconds on the 480×320 variant — longer than the
     * 5 s WDT window.  Remove the main task from the WDT for the duration of
     * the initial render, then re-add and reset immediately after. */
    ESP_ERROR_CHECK(esp_task_wdt_delete(NULL));
    lcd_log_init("VIEWOWL " FIRMWARE_VERSION_NUMVER);
    ESP_ERROR_CHECK(esp_task_wdt_add(NULL));
    ESP_ERROR_CHECK(esp_task_wdt_reset());

    /* ── Provisioning check ─────────────────────────────────────────────
     * If either the token or the WiFi credentials are missing from NVS the
     * device waits up to 2 minutes for the browser to send all three over
     * USB Serial (VIEWOWL_TOKEN, VIEWOWL_SSID, VIEWOWL_PASS).
     * On success it reboots with all credentials stored.  On timeout it falls
     * through and boots with the compile-time defaults from config.h. */
    if (!nvs_storage_has_token() || !nvs_storage_has_wifi()) {
        lcd_log_show_wait("NO CREDENTIALS",
                          "Connect USB cable",
                          "Open dashboard BURN wizard");

        ESP_LOGW(TAG, "Token or WiFi missing from NVS — entering provisioning mode");

        /* Provisioning can take up to 2 minutes — remove main task from the
         * WDT so it does not trigger while we are blocked in serial_provision_wait.
         * Re-added immediately after so normal operation is still guarded. */
        ESP_ERROR_CHECK(esp_task_wdt_delete(NULL));

        bool provisioned = serial_provision_wait(120000);

        ESP_ERROR_CHECK(esp_task_wdt_add(NULL));
        ESP_ERROR_CHECK(esp_task_wdt_reset());

        lcd_log_hide_wait();

        if (provisioned) {
            lcd_log_info("Credentials stored — rebooting...");
            vTaskDelay(pdMS_TO_TICKS(500));
            esp_restart();
        }

        /* Timed out — continue with the compile-time defaults. */
        lcd_log_system("Timeout — using built-in defaults");
    }

    /* ── Resolve WiFi credentials ────────────────────────────────────────
     * Prefer NVS-stored credentials provisioned via serial.  Fall back to
     * the compile-time defaults from config.h so development builds that
     * were never provisioned still connect without manual NVS setup. */
    char wifi_ssid[NVS_SSID_MAX_LEN + 1] = WIFI_SSID;
    char wifi_pass[NVS_PASS_MAX_LEN + 1] = WIFI_PASS;

    if (nvs_storage_read_ssid(wifi_ssid, sizeof(wifi_ssid)) != ESP_OK) {
        ESP_LOGW(TAG, "NVS SSID missing — using built-in default");
    }
    if (nvs_storage_read_pass(wifi_pass, sizeof(wifi_pass)) != ESP_OK) {
        ESP_LOGW(TAG, "NVS password missing — using built-in default");
    }

    lcd_log_system("LCD ready");
    lcd_log_system("Connecting to Wi-Fi...");
    ESP_LOGI(TAG, "Connecting to SSID: %s", wifi_ssid);

    wifi_init_sta(wifi_ssid, wifi_pass);

    lcd_log_info("Wi-Fi connected");
    lcd_log_system("Starting UDP client task");
    lcd_log_hint("Server: " SERVER_IP ":" STRINGIFY(SERVER_PORT));

    xTaskCreate(udp_client_task, "udp_client", 8192, NULL, 5, NULL);

    last_success_for_WDT = esp_timer_get_time();

    while (1) {
        vTaskDelay(pdMS_TO_TICKS(1000));

        ESP_ERROR_CHECK(esp_task_wdt_reset());

        /* 10 minutes without a successful frame render → hard reset. */
        if (last_success_for_WDT < esp_timer_get_time() - 10 * 60 * 1000000LL) {
            ESP_LOGE(TAG, "No successful frame for 10 minutes — triggering restart");
            lcd_log_error("Watchdog: no frame for 10 min — restarting");
            vTaskDelay(pdMS_TO_TICKS(200)); /* let LCD DMA finish */
            esp_restart();
        }

        s_maincount++;
        if (s_maincount > 10) {
            ESP_LOGI(TAG, "main loop heartbeat");
            s_maincount = 0;
        }
    }
}
