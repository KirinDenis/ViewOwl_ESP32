#include "udp_protocol.h"
#include "nvs_storage.h"
#include "config.h"
#include "lcd_log.h"
#include "esp_wifi.h"
#include "esp_mac.h"

#include <inttypes.h>
#include <string.h>
#include <sys/socket.h>
#include <netinet/in.h>

#include "esp_log.h"
#include "esp_system.h"
#include "esp_heap_caps.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

static const char *TAG = "udp_protocol";

/* Current PING interval — may be updated at runtime by PACKET_CONFIG. */
static volatile uint16_t s_ping_interval_s = PING_INTERVAL_S;

/* ── Public ───────────────────────────────────────────────────────────── */

/* Converts a 32-char hex string (GUID byte order) to 16 binary bytes. */
static void hex_to_token_bytes(const char *hex, uint8_t out[16])
{
    for (int i = 0; i < 16; i++) {
        uint8_t hi = (uint8_t)(hex[i * 2]);
        uint8_t lo = (uint8_t)(hex[i * 2 + 1]);

        hi = (uint8_t)((hi >= 'a') ? hi - 'a' + 10 :
                       (hi >= 'A') ? hi - 'A' + 10 : hi - '0');
        lo = (uint8_t)((lo >= 'a') ? lo - 'a' + 10 :
                       (lo >= 'A') ? lo - 'A' + 10 : lo - '0');

        out[i] = (uint8_t)((hi << 4) | lo);
    }
}

void udp_protocol_send_hello(int sock, const struct sockaddr_in *dest,
                              uint8_t *buf, uint32_t frame_crc)
{
    /* Try to load the token from NVS (written during provisioning).
     * Fall back to the compile-time TOKEN_BYTES from config.h when no
     * provisioned token is found — useful during development. */
    static const uint8_t default_token_bytes[16] = TOKEN_BYTES;
    uint8_t token_bytes[16];

    char hex[NVS_TOKEN_HEX_LEN + 1];
    if (nvs_storage_read_token(hex, sizeof(hex)) == ESP_OK) {
        hex_to_token_bytes(hex, token_bytes);
    } else {
        memcpy(token_bytes, default_token_bytes, 16);
    }

    hello_payload_t payload;
    memset(&payload, 0, sizeof(payload));
    memcpy(payload.token, token_bytes, sizeof(token_bytes));
    payload.display_type_id        = DISPLAY_TYPE_ID;
    payload.display_width          = LCD_H_RES;
    payload.display_height         = LCD_V_RES;
    payload.firmware_version_major = FIRMWARE_VERSION_MAJOR;
    payload.firmware_version_minor = FIRMWARE_VERSION_MINOR;
    payload.firmware_version_patch = FIRMWARE_VERSION_PATCH;
    payload.reserved               = 0;
    esp_read_mac(payload.mac, ESP_MAC_WIFI_STA); /* WiFi STA MAC — hardware fingerprint */

    /* seq carries the CRC32 of the last rendered frame (0 = no frame yet).
     * The server compares it with the on-disk file CRC and replies with
     * PACKET_NOT_MODIFIED when the frame has not changed since last download. */
    packet_header_t hdr = {
        .magic          = MAGIC_TOKEN,
        .packet_type    = PACKET_HELLO,
        .session_id     = 0,
        .seq            = frame_crc,
        .payload_length = 0,
        .flags          = 0,
    };

    int pkt_len = write_packet(buf, &hdr, (const uint8_t *)&payload, sizeof(payload));
    if (pkt_len < 0) {
        ESP_LOGE(TAG, "send_hello: write_packet failed (%d)", pkt_len);
        return;
    }

    ESP_LOGI(TAG, "HELLO sent (display=%dx%d type=%d fw=%d.%d.%d crc=0x%08" PRIx32 " mac=%02X:%02X:%02X:%02X:%02X:%02X)",
             LCD_H_RES, LCD_V_RES, DISPLAY_TYPE_ID,
             FIRMWARE_VERSION_MAJOR, FIRMWARE_VERSION_MINOR, FIRMWARE_VERSION_PATCH,
             frame_crc,
             payload.mac[0], payload.mac[1], payload.mac[2],
             payload.mac[3], payload.mac[4], payload.mac[5]);

    sendto(sock, buf, pkt_len, 0, (const struct sockaddr *)dest, sizeof(*dest));
}

void udp_protocol_send_ack(int sock, const struct sockaddr_in *dest,
                            uint8_t *buf, uint16_t session_id, uint32_t seq)
{
    packet_header_t ack = {
        .magic          = MAGIC_TOKEN,
        .packet_type    = PACKET_ACK,
        .session_id     = session_id,
        .seq            = seq,
        .payload_length = 0,
        .flags          = 0,
    };

    int ack_len = write_packet(buf, &ack, NULL, 0);
    if (ack_len < 0) {
        ESP_LOGE(TAG, "send_ack: write_packet failed (%d)", ack_len);
        return;
    }

    sendto(sock, buf, ack_len, 0, (const struct sockaddr *)dest, sizeof(*dest));
}

void udp_protocol_send_ping(int sock, const struct sockaddr_in *dest,
                             uint8_t *buf, uint32_t uptime_s)
{
    int8_t rssi = 0;
    wifi_ap_record_t ap_info;
    if (esp_wifi_sta_get_ap_info(&ap_info) == ESP_OK) {
        rssi = ap_info.rssi;
    }

    ping_payload_t ping = {
        .uptime    = uptime_s,
        .free_heap = esp_get_free_heap_size(),
        .wifi_rssi = rssi,
        .reserved  = 0,
    };

    packet_header_t hdr = {
        .magic          = MAGIC_TOKEN,
        .packet_type    = PACKET_PING,
        .session_id     = 0,  /* PING is sent outside of a session */
        .seq            = 0,
        .payload_length = 0,
        .flags          = 0,
    };

    int pkt_len = write_packet(buf, &hdr, (const uint8_t *)&ping, sizeof(ping));
    if (pkt_len < 0) {
        ESP_LOGE(TAG, "send_ping: write_packet failed (%d)", pkt_len);
        return;
    }

    sendto(sock, buf, pkt_len, 0, (const struct sockaddr *)dest, sizeof(*dest));
    ESP_LOGI(TAG, "PING sent: uptime=%" PRIu32 "s heap=%" PRIu32, uptime_s, ping.free_heap);
}

void udp_protocol_handle_config(const packet_header_t *hdr, const uint8_t *payload)
{
    if (payload && hdr->payload_length >= sizeof(config_payload_t)) {
        config_payload_t cfg;
        memcpy(&cfg, payload, sizeof(cfg));

        if (cfg.ping_interval_s > 0) {
            ESP_LOGI(TAG, "CONFIG: new ping interval = %us", cfg.ping_interval_s);
            s_ping_interval_s = cfg.ping_interval_s;
        }
    }

    if (hdr->flags & FLAG_RESTART) {
        ESP_LOGW(TAG, "CONFIG: FLAG_RESTART received — rebooting...");
        vTaskDelay(pdMS_TO_TICKS(100)); /* let the log flush */
        esp_restart();
    }
}

uint16_t udp_protocol_get_ping_interval(void)
{
    return s_ping_interval_s;
}
