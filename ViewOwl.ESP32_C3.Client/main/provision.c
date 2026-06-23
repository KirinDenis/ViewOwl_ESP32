/**
 * @file provision.c
 * @brief Serial provisioning for the ESP32-C3 client (USB-Serial-JTAG console).
 *
 * C3 port of ../../ViewOwl.ESP32.Client/main/serial_provision.c. The line
 * protocol and NVS writes are identical (must match BurnModal.jsx); only the
 * transport changes: USB-Serial-JTAG instead of UART0, and no LCD coupling.
 */

#include "provision.h"
#include "nvs_storage.h"           /* shared from the classic client */
#include "lcd_gc9a01.h"            /* on-screen diagnostics (no serial visibility here) */
#include "config.h"

#include <string.h>
#include <stdio.h>
#include <inttypes.h>
#include <fcntl.h>
#include <unistd.h>
#include "driver/usb_serial_jtag.h"
#include "driver/usb_serial_jtag_vfs.h"   /* IDF 5.3+: route stdin/stdout via the driver */
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

static const char *TAG = "provision";

/* -- On-screen diagnostics -------------------------------------------------
 * The browser owns the serial port during provisioning, so the usual log
 * monitor can't be attached. Show progress on the round display instead:
 *   • 3 slots (TOKEN / SSID / PASS): grey = waiting, green = received
 *   • heartbeat dot: toggles on EVERY received byte -> proves data is flowing in
 *   • a "ready-sent" tick that blinks each VIEWOWL_READY broadcast
 * Diagnosis: no heartbeat ever = USB-JTAG read not delivering bytes (transport);
 *            heartbeat but no green slots = protocol/prefix mismatch.            */
#define COL_SLOT_OFF 0x4208   /* dim grey */
#define COL_SLOT_ON  0x07E0   /* green    */
#define COL_HB_A     0xFFE0   /* yellow   */
#define COL_HB_B     0x0008   /* bg       */
#define COL_TICK     0x07FF   /* cyan     */

static void diag_slot(int idx, uint16_t colour)   /* idx 0=token 1=ssid 2=pass */
{
    lcd_gc9a01_fill_rect(80 + idx * 40 - 10, 150, 20, 20, colour);
}

static void diag_heartbeat(void)
{
    static bool on = false;
    on = !on;
    lcd_gc9a01_fill_circle(120, 192, 8, on ? COL_HB_A : COL_HB_B);
}

static void diag_ready_tick(void)
{
    static bool on = false;
    on = !on;
    lcd_gc9a01_fill_rect(110, 56, 20, 6, on ? COL_TICK : COL_HB_B);
}

#define RX_BUF_SIZE        256
#define READY_INTERVAL_MS  3000
#define POLL_MS            50

/* Serial line prefixes - MUST match BurnModal.jsx (and the classic client). */
#define PREFIX_TOKEN  "VIEWOWL_TOKEN:"
#define PREFIX_SSID   "VIEWOWL_WIFI_SSID:"
#define PREFIX_PASS   "VIEWOWL_WIFI_PASS:"
#define PREFIX_TOKEN_LEN  14
#define PREFIX_SSID_LEN   18
#define PREFIX_PASS_LEN   18

static bool is_hex_char(char c)
{
    return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}

/* Write via stdout - the console TX path that is confirmed working (logs come
 * through). usb_serial_jtag_write_bytes proved unreliable in reaching the host. */
static void serial_send(const char *msg)
{
    fputs(msg, stdout);
    fflush(stdout);
}

bool provision_wait(uint32_t timeout_ms)
{
    usb_serial_jtag_driver_config_t cfg = {
        .rx_buffer_size = RX_BUF_SIZE * 2,
        .tx_buffer_size = RX_BUF_SIZE * 2,
    };
    esp_err_t ret = usb_serial_jtag_driver_install(&cfg);
    if (ret != ESP_OK) {
        ESP_LOGE(TAG, "usb_serial_jtag_driver_install failed: 0x%x", (unsigned)ret);
        return false;
    }

    /* Route stdin/stdout through the interrupt driver - without this the console
     * read path returns nothing on IDF >= 5.1.2 (known regression). */
    usb_serial_jtag_vfs_use_driver();
    int fl = fcntl(STDIN_FILENO, F_GETFL, 0);
    fcntl(STDIN_FILENO, F_SETFL, fl | O_NONBLOCK);   /* non-blocking poll reads */

    ESP_LOGI(TAG, "Entering provisioning mode (timeout=%" PRIu32 " ms)", timeout_ms);

    char token_buf[NVS_TOKEN_HEX_LEN + 1] = {0};
    char ssid_buf[NVS_SSID_MAX_LEN + 1]   = {0};
    char pass_buf[NVS_PASS_MAX_LEN + 1]   = {0};
    bool got_token = false, got_ssid = false, got_pass = false;

    char     line[RX_BUF_SIZE];
    int      pos        = 0;
    uint32_t elapsed_ms = 0;
    uint32_t last_ready = READY_INTERVAL_MS;

    for (int i = 0; i < 3; i++) diag_slot(i, COL_SLOT_OFF);   /* waiting */

    while (elapsed_ms < timeout_ms) {

        if (elapsed_ms - last_ready >= READY_INTERVAL_MS || elapsed_ms < POLL_MS) {
            serial_send("VIEWOWL_READY\n");
            diag_ready_tick();
            last_ready = elapsed_ms;
        }

        uint8_t byte = 0;
        int n = read(STDIN_FILENO, &byte, 1);   /* non-blocking via console VFS */
        if (n != 1) {
            vTaskDelay(pdMS_TO_TICKS(POLL_MS));
            elapsed_ms += POLL_MS;
            continue;
        }
        elapsed_ms += POLL_MS;
        diag_heartbeat();                 /* a byte arrived from the browser */
        if ((char)byte == '\r') continue;

        if ((char)byte == '\n') {
            line[pos] = '\0';
            pos = 0;
            if (strlen(line) == 0) continue;

            if (strncmp(line, PREFIX_TOKEN, PREFIX_TOKEN_LEN) == 0) {
                const char *hex = line + PREFIX_TOKEN_LEN;
                if (strlen(hex) != NVS_TOKEN_HEX_LEN) {
                    serial_send("VIEWOWL_TOKEN_ERR:bad_length\n"); continue;
                }
                bool valid = true;
                for (size_t i = 0; i < NVS_TOKEN_HEX_LEN; i++) {
                    if (!is_hex_char(hex[i])) { valid = false; break; }
                }
                if (!valid) { serial_send("VIEWOWL_TOKEN_ERR:invalid_chars\n"); continue; }
                strncpy(token_buf, hex, NVS_TOKEN_HEX_LEN);
                token_buf[NVS_TOKEN_HEX_LEN] = '\0';
                got_token = true;
                diag_slot(0, COL_SLOT_ON);
                serial_send("VIEWOWL_TOKEN_OK\n");
                ESP_LOGI(TAG, "Token received");

            } else if (strncmp(line, PREFIX_SSID, PREFIX_SSID_LEN) == 0) {
                const char *val = line + PREFIX_SSID_LEN;
                size_t len = strlen(val);
                if (len == 0 || len > NVS_SSID_MAX_LEN) {
                    serial_send("VIEWOWL_WIFI_SSID_ERR:bad_length\n"); continue;
                }
                strncpy(ssid_buf, val, NVS_SSID_MAX_LEN);
                ssid_buf[NVS_SSID_MAX_LEN] = '\0';
                got_ssid = true;
                diag_slot(1, COL_SLOT_ON);
                serial_send("VIEWOWL_WIFI_SSID_OK\n");
                ESP_LOGI(TAG, "SSID received");

            } else if (strncmp(line, PREFIX_PASS, PREFIX_PASS_LEN) == 0) {
                const char *val = line + PREFIX_PASS_LEN;
                if (strlen(val) > NVS_PASS_MAX_LEN) {
                    serial_send("VIEWOWL_WIFI_PASS_ERR:too_long\n"); continue;
                }
                strncpy(pass_buf, val, NVS_PASS_MAX_LEN);
                pass_buf[NVS_PASS_MAX_LEN] = '\0';
                got_pass = true;
                diag_slot(2, COL_SLOT_ON);
                serial_send("VIEWOWL_WIFI_PASS_OK\n");
                ESP_LOGI(TAG, "Password received");

            } else if (strncmp(line, "VIEWOWL_PIN_", 12) == 0) {
                /* Pin config the dashboard sends before WiFi - the C3 board has
                 * fixed pins, so ignore it (the browser doesn't read a response). */
                continue;

            } else {
                serial_send("VIEWOWL_TOKEN_ERR:unexpected_line\n");
                continue;
            }

            if (got_token && got_ssid && got_pass) {
                esp_err_t w = nvs_storage_write_token(token_buf);
                if (w == ESP_OK) w = nvs_storage_write_wifi(ssid_buf, pass_buf);
                if (w != ESP_OK) {
                    serial_send("VIEWOWL_TOKEN_ERR:nvs_write_failed\n");
                    ESP_LOGE(TAG, "NVS write error: 0x%x", (unsigned)w);
                    usb_serial_jtag_driver_uninstall();
                    return false;
                }
                serial_send("VIEWOWL_PROV_DONE\n");
                ESP_LOGI(TAG, "Provisioning complete - token + WiFi stored");
                usb_serial_jtag_driver_uninstall();
                return true;
            }

        } else if (pos < RX_BUF_SIZE - 1) {
            line[pos++] = (char)byte;
        } else {
            pos = 0; /* line too long - reset */
        }
    }

    serial_send("VIEWOWL_TOKEN_ERR:timeout\n");
    ESP_LOGW(TAG, "Provisioning timed out after %" PRIu32 " ms", timeout_ms);
    usb_serial_jtag_driver_uninstall();
    return false;
}
