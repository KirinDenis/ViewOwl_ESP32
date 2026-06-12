#include "serial_provision.h"
#include "nvs_storage.h"
#include "lcd_log.h"

#include <string.h>
#include <stdint.h>
#include "driver/uart.h"
#include "esp_log.h"
#include "esp_task_wdt.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

static const char *TAG = "serial_provision";

#define UART_NUM           UART_NUM_0
#define RX_BUF_SIZE        256
#define READY_INTERVAL_MS  3000
#define POLL_MS            50

/* Serial line prefixes — must match BurnModal.jsx exactly. */
#define PREFIX_TOKEN  "VIEWOWL_TOKEN:"
#define PREFIX_SSID   "VIEWOWL_WIFI_SSID:"
#define PREFIX_PASS   "VIEWOWL_WIFI_PASS:"

#define PREFIX_TOKEN_LEN  14   /* strlen("VIEWOWL_TOKEN:") */
#define PREFIX_SSID_LEN   18   /* strlen("VIEWOWL_WIFI_SSID:") */
#define PREFIX_PASS_LEN   18   /* strlen("VIEWOWL_WIFI_PASS:") */

/* ── Private helpers ──────────────────────────────────────────────────── */

static bool is_hex_char(char c)
{
    return (c >= '0' && c <= '9') ||
           (c >= 'a' && c <= 'f') ||
           (c >= 'A' && c <= 'F');
}

static void uart_send(const char *msg)
{
    uart_write_bytes(UART_NUM, msg, strlen(msg));
}

/* ── Public ───────────────────────────────────────────────────────────── */

bool serial_provision_wait(uint32_t timeout_ms)
{
    /* Install the UART driver for RX.  IDF uses UART0 for logging but does
     * not install the driver — we install it here for the provisioning window
     * then delete it so normal logging resumes after. */
    esp_err_t install_ret = uart_driver_install(
        UART_NUM,
        RX_BUF_SIZE * 2,
        0,     /* tx buffer: 0 = blocking, fine for short messages */
        0, NULL, 0);

    if (install_ret != ESP_OK) {
        ESP_LOGE(TAG, "uart_driver_install failed: 0x%x", (unsigned)install_ret);
        return false;
    }

    uart_flush_input(UART_NUM);
    ESP_LOGI(TAG, "Entering provisioning mode (timeout=%" PRIu32 " ms)", timeout_ms);

    /* Receive buffers — filled as each line arrives. */
    char token_buf[NVS_TOKEN_HEX_LEN + 1] = {0};
    char ssid_buf[NVS_SSID_MAX_LEN + 1]   = {0};
    char pass_buf[NVS_PASS_MAX_LEN + 1]   = {0};

    bool got_token = false;
    bool got_ssid  = false;
    bool got_pass  = false;

    char     line[RX_BUF_SIZE];
    int      pos        = 0;
    uint32_t elapsed_ms = 0;
    uint32_t last_ready = READY_INTERVAL_MS; /* send READY immediately */

    while (elapsed_ms < timeout_ms) {

        /* Broadcast VIEWOWL_READY every 3 s. */
        if (elapsed_ms - last_ready >= READY_INTERVAL_MS ||
            elapsed_ms < POLL_MS) {
            uart_send("VIEWOWL_READY\n");
            last_ready = elapsed_ms;
        }

        /* NOTE: do NOT call esp_task_wdt_reset() here — the main task is
         * removed from the WDT by the caller before entering this function.
         * Calling reset on an unregistered task logs an error every POLL_MS. */

        /* Animate the wait dialog spinner every ~200 ms (4 × POLL_MS). */
        if ((elapsed_ms % 200) < POLL_MS) {
            lcd_log_spin();
        }

        uint8_t byte = 0;
        int n = uart_read_bytes(UART_NUM, &byte, 1, pdMS_TO_TICKS(POLL_MS));
        elapsed_ms += POLL_MS;

        if (n <= 0) continue;
        if ((char)byte == '\r') continue;

        if ((char)byte == '\n') {
            line[pos] = '\0';
            pos = 0;

            if (strlen(line) == 0) continue;

            ESP_LOGD(TAG, "RX: %s", line);

            /* ── TOKEN ──────────────────────────────────────────────── */
            if (strncmp(line, PREFIX_TOKEN, PREFIX_TOKEN_LEN) == 0) {
                const char *hex = line + PREFIX_TOKEN_LEN;
                size_t      len = strlen(hex);

                if (len != NVS_TOKEN_HEX_LEN) {
                    uart_send("VIEWOWL_TOKEN_ERR:bad_length\n");
                    ESP_LOGW(TAG, "Token wrong length: %u", (unsigned)len);
                    continue;
                }
                bool valid = true;
                for (size_t i = 0; i < NVS_TOKEN_HEX_LEN; i++) {
                    if (!is_hex_char(hex[i])) { valid = false; break; }
                }
                if (!valid) {
                    uart_send("VIEWOWL_TOKEN_ERR:invalid_chars\n");
                    ESP_LOGW(TAG, "Token contains non-hex characters");
                    continue;
                }
                strncpy(token_buf, hex, NVS_TOKEN_HEX_LEN);
                token_buf[NVS_TOKEN_HEX_LEN] = '\0';
                got_token = true;
                uart_send("VIEWOWL_TOKEN_OK\n");
                ESP_LOGI(TAG, "Token received");

            /* ── SSID ───────────────────────────────────────────────── */
            } else if (strncmp(line, PREFIX_SSID, PREFIX_SSID_LEN) == 0) {
                const char *val = line + PREFIX_SSID_LEN;
                size_t      len = strlen(val);

                if (len == 0 || len > NVS_SSID_MAX_LEN) {
                    uart_send("VIEWOWL_WIFI_SSID_ERR:bad_length\n");
                    ESP_LOGW(TAG, "SSID length invalid: %u", (unsigned)len);
                    continue;
                }
                strncpy(ssid_buf, val, NVS_SSID_MAX_LEN);
                ssid_buf[NVS_SSID_MAX_LEN] = '\0';
                got_ssid = true;
                uart_send("VIEWOWL_WIFI_SSID_OK\n");
                ESP_LOGI(TAG, "SSID received");

            /* ── PASS ───────────────────────────────────────────────── */
            } else if (strncmp(line, PREFIX_PASS, PREFIX_PASS_LEN) == 0) {
                const char *val = line + PREFIX_PASS_LEN;
                size_t      len = strlen(val);

                if (len > NVS_PASS_MAX_LEN) {
                    uart_send("VIEWOWL_WIFI_PASS_ERR:too_long\n");
                    ESP_LOGW(TAG, "Password too long: %u", (unsigned)len);
                    continue;
                }
                strncpy(pass_buf, val, NVS_PASS_MAX_LEN);
                pass_buf[NVS_PASS_MAX_LEN] = '\0';
                got_pass = true;
                uart_send("VIEWOWL_WIFI_PASS_OK\n");
                ESP_LOGI(TAG, "Password received");

            } else {
                uart_send("VIEWOWL_TOKEN_ERR:unexpected_line\n");
                ESP_LOGW(TAG, "Unexpected line ignored");
                continue;
            }

            /* All three received — write to NVS and finish. */
            if (got_token && got_ssid && got_pass) {
                esp_err_t ret = nvs_storage_write_token(token_buf);
                if (ret == ESP_OK) {
                    ret = nvs_storage_write_wifi(ssid_buf, pass_buf);
                }
                if (ret != ESP_OK) {
                    uart_send("VIEWOWL_TOKEN_ERR:nvs_write_failed\n");
                    ESP_LOGE(TAG, "NVS write error: 0x%x", (unsigned)ret);
                    uart_driver_delete(UART_NUM);
                    return false;
                }
                uart_send("VIEWOWL_PROV_DONE\n");
                ESP_LOGI(TAG, "Provisioning complete — token + WiFi stored");
                uart_driver_delete(UART_NUM);
                return true;
            }

        } else {
            if (pos < RX_BUF_SIZE - 1) {
                line[pos++] = (char)byte;
            } else {
                pos = 0; /* line too long — reset parser */
            }
        }
    }

    uart_send("VIEWOWL_TOKEN_ERR:timeout\n");
    ESP_LOGW(TAG, "Provisioning timed out after %" PRIu32 " ms", timeout_ms);
    uart_driver_delete(UART_NUM);
    return false;
}
