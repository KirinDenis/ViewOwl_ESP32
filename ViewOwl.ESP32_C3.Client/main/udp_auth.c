/**
 * @file udp_auth.c
 * @brief M3a - minimal HELLO/AUTH against the ViewOwl UDP server.
 *
 * Uses the SHARED wire contract (packet.h from the classic client). Builds a
 * HELLO with this device's token + 240x240 capability, sends it to
 * SERVER_IP:SERVER_PORT, and reports whether the server accepted it (AUTH) or
 * rejected it (ERROR, e.g. 0x02 = token not registered).
 */

#include "udp_auth.h"
#include "packet.h"          /* shared contract */
#include "config.h"
#include "nvs_storage.h"

#include <string.h>
#include "esp_log.h"
#include "esp_mac.h"
#include "lwip/sockets.h"
#include "lwip/inet.h"

static const char *TAG = "udp_auth";

/* Parse 2*len hex chars into len bytes. Returns 0 on success. */
static int hex2bytes(const char *hex, uint8_t *out, int len)
{
    for (int i = 0; i < len; i++) {
        char hi = hex[i * 2], lo = hex[i * 2 + 1];
        int h = (hi >= '0' && hi <= '9') ? hi - '0' :
                (hi >= 'a' && hi <= 'f') ? hi - 'a' + 10 :
                (hi >= 'A' && hi <= 'F') ? hi - 'A' + 10 : -1;
        int l = (lo >= '0' && lo <= '9') ? lo - '0' :
                (lo >= 'a' && lo <= 'f') ? lo - 'a' + 10 :
                (lo >= 'A' && lo <= 'F') ? lo - 'A' + 10 : -1;
        if (h < 0 || l < 0) return -1;
        out[i] = (uint8_t)((h << 4) | l);
    }
    return 0;
}

bool udp_auth_test(void)
{
    /* Resolve the 16-byte token: NVS (provisioned, hex, GUID byte order) -> config.h fallback. */
    uint8_t token[16];
    char hex[NVS_TOKEN_HEX_LEN + 1] = {0};
    if (nvs_storage_read_token(hex, sizeof(hex)) == ESP_OK && hex2bytes(hex, token, 16) == 0) {
        ESP_LOGI(TAG, "Using NVS token");
    } else {
        uint8_t fallback[16] = TOKEN_BYTES;
        memcpy(token, fallback, 16);
        ESP_LOGW(TAG, "Using config.h TOKEN_BYTES fallback");
    }

    /* Build HELLO payload. */
    hello_payload_t hp = {0};
    memcpy(hp.token, token, 16);
    hp.display_type_id        = DISPLAY_TYPE_ID;
    hp.display_width          = LCD_H_RES;
    hp.display_height         = LCD_V_RES;
    hp.firmware_version_major = FIRMWARE_VERSION_MAJOR;
    hp.firmware_version_minor = FIRMWARE_VERSION_MINOR;
    hp.firmware_version_patch = FIRMWARE_VERSION_PATCH;
    esp_read_mac(hp.mac, ESP_MAC_WIFI_STA);

    packet_header_t hdr = {
        .magic       = MAGIC_TOKEN,
        .packet_type = PACKET_HELLO,
        .session_id  = 0,
        .seq         = 0,
        .flags       = 0,
    };
    static uint8_t buf[PACKET_SIZE];   /* static: 1400 B off the small main-task stack */
    int len = write_packet(buf, &hdr, (const uint8_t *)&hp, sizeof(hp));
    if (len < 0) { ESP_LOGE(TAG, "write_packet failed"); return false; }

    /* UDP socket -> SERVER_IP:SERVER_PORT, 5 s receive timeout. */
    int sock = socket(AF_INET, SOCK_DGRAM, 0);
    if (sock < 0) { ESP_LOGE(TAG, "socket() failed"); return false; }

    struct sockaddr_in dest = {
        .sin_family = AF_INET,
        .sin_port   = htons(SERVER_PORT),
        .sin_addr.s_addr = inet_addr(SERVER_IP),
    };
    struct timeval tv = { .tv_sec = 5, .tv_usec = 0 };
    setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));

    ESP_LOGI(TAG, "HELLO -> %s:%d (%d bytes)", SERVER_IP, SERVER_PORT, len);
    int sent = sendto(sock, buf, len, 0, (struct sockaddr *)&dest, sizeof(dest));
    if (sent < 0) { ESP_LOGE(TAG, "sendto() failed errno=%d", errno); close(sock); return false; }

    static uint8_t rbuf[PACKET_SIZE];  /* static: another 1400 B off the stack */
    int n = recvfrom(sock, rbuf, sizeof(rbuf), 0, NULL, NULL);
    close(sock);

    if (n < (int)sizeof(packet_header_t)) {
        ESP_LOGE(TAG, "No/short reply (n=%d) - server unreachable or silent", n);
        return false;
    }

    packet_header_t rh;
    if (read_header(rbuf, n, &rh) != 0) {
        ESP_LOGE(TAG, "Reply bad magic / too short");
        return false;
    }

    if (rh.packet_type == PACKET_AUTH) {
        ESP_LOGI(TAG, "AUTH OK - session_id=%u", rh.session_id);
        return true;
    }
    if (rh.packet_type == PACKET_ERROR) {
        uint8_t code = (n > (int)sizeof(packet_header_t)) ? rbuf[sizeof(packet_header_t)] : 0;
        ESP_LOGE(TAG, "AUTH REJECTED - error code 0x%02X%s", code,
                 code == ERR_UNKNOWN_DEVICE ? " (token not registered)" : "");
        return false;
    }
    ESP_LOGW(TAG, "Unexpected reply type=%u", rh.packet_type);
    return false;
}
