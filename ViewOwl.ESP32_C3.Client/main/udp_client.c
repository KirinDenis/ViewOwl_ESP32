/**
 * @file udp_client.c
 * @brief ViewOwl ESP32-C3 client - frame receive + render, with Class-C playback.
 *
 * HELLO -> AUTH -> (DATA | BATCH_START) -> render.
 *   - Single-frame (Class A/B): DATA chunks -> assemble in RAM -> decode -> blit.
 *   - Class-C batch (M3c): BATCH_START -> stream DATA to the "frames" flash partition
 *     -> a player task loops the stored frames @ fps. The whole batch (~516 KB) does
 *     not fit C3 RAM, so frames live in flash and are read one at a time for playback.
 *
 * Sends PING heartbeats during idle so the server keeps the device Online.
 */

#include "udp_client.h"
#include "packet.h"
#include "config.h"
#include "nvs_storage.h"
#include "frame_decoder.h"   /* shared: LZ4+palette stream decoder */
#include "flash_frames.h"    /* shared: flash storage for Class-C frames */
#include "lcd_gc9a01.h"

#include <errno.h>
#include <string.h>

#include "esp_log.h"
#include "esp_mac.h"
#include "esp_system.h"
#include "esp_timer.h"
#include "esp_heap_caps.h"
#include "esp_rom_crc.h"

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/semphr.h"

#include "lwip/sockets.h"
#include "lwip/inet.h"

static const char *TAG = "udp_client";

#define MAX_FRAME_BYTES (IMAGE_SIZE + 4096u)

/* ── frame render: decode FRAME_FLAG_LZ4_PALETTE -> blit to the round ──────── */
#define DEC_STRIP_ROWS 40                                  /* 240 / 40 = 6 strips */

static uint8_t s_lz4_block[LZ4_BLOCK_SIZE];                /* per-block LZ4 scratch (4 KB) */
static uint8_t s_strip[LCD_H_RES * DEC_STRIP_ROWS * 2];    /* one strip, big-endian RGB565 */

static int render_lz4_palette(const uint8_t *comp, size_t comp_len)
{
    frame_lz4_stream_t s;
    if (frame_lz4_stream_init(&s, comp, comp_len,
                              (uint32_t)LCD_H_RES * LCD_V_RES, s_lz4_block) != 0) {
        return -1;
    }
    size_t px_per_strip = (size_t)LCD_H_RES * DEC_STRIP_ROWS;
    for (int y = 0; y < LCD_V_RES; y += DEC_STRIP_ROWS) {
        if (frame_lz4_stream_read(&s, s_strip, px_per_strip) <= 0) return -1;
        lcd_gc9a01_blit_be(0, y, LCD_H_RES, DEC_STRIP_ROWS, s_strip);
    }
    return 0;
}

/* ── Class-C player ──────────────────────────────────────────────────────────
 * A dedicated task loops the flash-stored frames. It is stopped during a batch
 * transfer (SPI must not contend with the receive loop) and re-started after the
 * batch commits. render_lz4_palette's static buffers are shared with the single-
 * frame path, but the player is always stopped before a single-frame render, so
 * the two never decode concurrently. */
static SemaphoreHandle_t s_player_sem = NULL;
static volatile bool     s_player_stop = false;

static void player_task_fn(void *arg)
{
    (void)arg;
    uint8_t *fbuf = NULL;
    uint32_t cap = 0;

    while (1) {
        xSemaphoreTake(s_player_sem, portMAX_DELAY);
        s_player_stop = false;

        if (!flash_frames_valid()) continue;

        uint8_t  count    = flash_frames_count();
        uint8_t  fps      = flash_frames_fps();
        uint32_t delay_ms = (fps > 0u) ? (1000u / fps) : 125u;
        uint32_t maxsz    = flash_frames_max_frame_size();
        if (count == 0u || maxsz == 0u) continue;

        if (!fbuf || cap < maxsz) {
            if (fbuf) heap_caps_free(fbuf);
            fbuf = heap_caps_malloc(maxsz, MALLOC_CAP_8BIT);
            cap  = fbuf ? maxsz : 0;
        }
        if (!fbuf) { ESP_LOGE(TAG, "player: alloc %u B failed", (unsigned)maxsz); continue; }

        ESP_LOGI(TAG, "player: %u frames @ %u fps (%u ms/frame)",
                 count, fps, (unsigned)delay_ms);

        uint8_t idx = 0;
        while (!s_player_stop) {
            size_t flen = 0;
            int64_t t0 = esp_timer_get_time();
            if (flash_frames_read(idx, fbuf, cap, &flen) == ESP_OK
                && flen > 0 && fbuf[0] == FRAME_FLAG_LZ4_PALETTE) {
                render_lz4_palette(fbuf, flen);
            }
#if PLAYER_FREE_RUN
            /* Free-run: as fast as the decode + SPI path allows; one-tick yield
             * keeps the idle task / Wi-Fi serviced (single-core C3). */
            (void)t0;
            idx = (uint8_t)((idx + 1u) % count);
            vTaskDelay(1);
#else
            /* Paced: honour the encoded fps, compensated for this frame's work;
             * floor at one tick so an overloaded device free-runs at its max. */
            uint32_t work_ms = (uint32_t)((esp_timer_get_time() - t0) / 1000);
            TickType_t st = (delay_ms > work_ms) ? pdMS_TO_TICKS(delay_ms - work_ms) : 0;
            if (st == 0) st = 1;
            idx = (uint8_t)((idx + 1u) % count);
            vTaskDelay(st);
#endif
        }
        ESP_LOGI(TAG, "player: stopped");
    }
}

static void frame_player_init(void)
{
    s_player_sem = xSemaphoreCreateBinary();
    configASSERT(s_player_sem);
    xTaskCreate(player_task_fn, "frame_player", 6144, NULL, 4, NULL);

    /* Resume playback if a valid batch survived the last power cycle. */
    if (flash_frames_valid()) {
        ESP_LOGI(TAG, "boot: valid flash frames - resuming playback");
        xSemaphoreGive(s_player_sem);
    }
}

/* ── small helpers ───────────────────────────────────────────────────────── */

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

static void resolve_token(uint8_t token[16])
{
    char hex[NVS_TOKEN_HEX_LEN + 1] = {0};
    if (nvs_storage_read_token(hex, sizeof(hex)) == ESP_OK && hex2bytes(hex, token, 16) == 0) {
        return;
    }
    uint8_t fallback[16] = TOKEN_BYTES;
    memcpy(token, fallback, 16);
    ESP_LOGW(TAG, "Using config.h TOKEN_BYTES fallback");
}

static void send_packet(int sock, const struct sockaddr_in *dest, uint8_t *buf,
                        uint8_t type, uint16_t session_id, uint32_t seq,
                        uint16_t flags, const uint8_t *payload, size_t payload_len)
{
    packet_header_t hdr = {
        .magic = MAGIC_TOKEN, .packet_type = type, .session_id = session_id,
        .seq = seq, .flags = flags,
    };
    int len = write_packet(buf, &hdr, payload, payload_len);
    if (len > 0) sendto(sock, buf, len, 0, (const struct sockaddr *)dest, sizeof(*dest));
}

static void send_hello(int sock, const struct sockaddr_in *dest, uint8_t *buf,
                       const uint8_t token[16], uint32_t frame_crc)
{
    hello_payload_t hp = {0};
    memcpy(hp.token, token, 16);
    hp.display_type_id        = DISPLAY_TYPE_ID;
    hp.display_width          = LCD_H_RES;
    hp.display_height         = LCD_V_RES;
    hp.firmware_version_major = FIRMWARE_VERSION_MAJOR;
    hp.firmware_version_minor = FIRMWARE_VERSION_MINOR;
    hp.firmware_version_patch = FIRMWARE_VERSION_PATCH;
    esp_read_mac(hp.mac, ESP_MAC_WIFI_STA);
    send_packet(sock, dest, buf, PACKET_HELLO, 0, frame_crc, 0,
                (const uint8_t *)&hp, sizeof(hp));
}

static void send_ack(int sock, const struct sockaddr_in *dest, uint8_t *buf,
                     uint16_t session_id, uint32_t seq)
{
    send_packet(sock, dest, buf, PACKET_ACK, session_id, seq, 0, NULL, 0);
}

static void send_ping(int sock, const struct sockaddr_in *dest, uint8_t *buf,
                      uint32_t uptime_s)
{
    ping_payload_t pp = {
        .uptime = uptime_s, .free_heap = (uint32_t)esp_get_free_heap_size(),
        .wifi_rssi = -50, .reserved = 0,
    };
    send_packet(sock, dest, buf, PACKET_PING, 0, 0, 0, (const uint8_t *)&pp, sizeof(pp));
}

/* ── post-AUTH transfer: single frame OR Class-C batch (to flash) ─────────────
 * Returns the CRC over all received payload bytes (for the next HELLO's NOT_MODIFIED),
 * or 0 when nothing renderable/committed was received. */
static uint32_t receive_and_render(int sock, const struct sockaddr_in *dest,
                                   uint8_t *sendbuf, uint8_t *recvbuf,
                                   uint16_t session_id, uint32_t total_size)
{
    uint8_t *buf = NULL;            /* single-frame buffer only */
    uint32_t cap = 0, recv_len = 0, crc = 0, expected = 0;
    int      is_batch = -1, ack_to = 0;
    bool     batch_ok = false;
    struct sockaddr_in src;
    socklen_t sl;

    while (1) {
        sl = sizeof(src);
        int n = recvfrom(sock, recvbuf, PACKET_SIZE, 0, (struct sockaddr *)&src, &sl);
        if (n <= 0) {
            if (ack_to++ < ACK_RETRY_MAX && expected > 0) {
                send_ack(sock, dest, sendbuf, session_id, expected - 1);
                continue;
            }
            ESP_LOGW(TAG, "transfer stalled (seq=%u)", (unsigned)expected);
            break;
        }
        ack_to = 0;

        packet_header_t h;
        if (read_header(recvbuf, n, &h) != 0) break;

        if (is_batch == -1) {
            if (h.packet_type == PACKET_BATCH_START) {
                if (h.payload_length < (uint16_t)sizeof(batch_start_payload_t)) break;
                batch_start_payload_t bsp;
                memcpy(&bsp, recvbuf + sizeof(packet_header_t), sizeof(bsp));
                ESP_LOGI(TAG, "BATCH_START: %u frames @ %u fps", bsp.frame_count, bsp.fps);

                s_player_stop = true;                   /* stop playback before flash write */
                vTaskDelay(pdMS_TO_TICKS(150));

                esp_err_t e = flash_frames_write_begin(bsp.frame_count, bsp.fps, bsp.frame_sizes);
                if (e != ESP_OK) {
                    ESP_LOGE(TAG, "flash_frames_write_begin: %s", esp_err_to_name(e));
                    send_ack(sock, dest, sendbuf, session_id, 0);
                    break;
                }
                is_batch = 1;
                expected = 0;
                send_ack(sock, dest, sendbuf, session_id, 0);
                continue;
            }
            if (h.packet_type == PACKET_DATA) {
                if (total_size == 0 || total_size > MAX_FRAME_BYTES) {
                    ESP_LOGE(TAG, "bad total_size %u", (unsigned)total_size); break;
                }
                cap = total_size;
                buf = heap_caps_malloc(cap, MALLOC_CAP_8BIT);
                if (!buf) { ESP_LOGE(TAG, "frame alloc %u failed", (unsigned)cap); break; }
                is_batch = 0;
                /* fall through to process this first DATA packet */
            } else {
                ESP_LOGW(TAG, "unexpected post-AUTH pkt 0x%02X", h.packet_type);
                break;
            }
        }

        if (is_batch == 1 && h.packet_type == PACKET_BATCH_COMMIT) {
            esp_err_t e = flash_frames_write_commit();
            send_ack(sock, dest, sendbuf, session_id, 0);
            if (e == ESP_OK) {
                ESP_LOGI(TAG, "BATCH committed - starting playback");
                batch_ok = true;
                s_player_stop = false;
                xSemaphoreGive(s_player_sem);
            } else {
                ESP_LOGE(TAG, "flash_frames_write_commit: %s", esp_err_to_name(e));
            }
            break;
        }
        if (h.packet_type != PACKET_DATA) break;
        if (h.session_id  != session_id)  continue;

        if (h.seq < expected) { send_ack(sock, dest, sendbuf, session_id, h.seq); continue; }
        if (h.seq > expected) { if (expected > 0) send_ack(sock, dest, sendbuf, session_id, expected - 1); continue; }

        const uint8_t *pl = recvbuf + sizeof(packet_header_t);
        uint16_t pl_len = h.payload_length;

        if (is_batch == 1) {
            esp_err_t e = flash_frames_write_chunk(pl, pl_len);
            if (e != ESP_OK) ESP_LOGE(TAG, "flash_frames_write_chunk: %s", esp_err_to_name(e));
        } else if (recv_len + pl_len <= cap) {
            memcpy(buf + recv_len, pl, pl_len);
            recv_len += pl_len;
        }
        crc = esp_rom_crc32_le(crc, pl, pl_len);
        expected++;
        send_ack(sock, dest, sendbuf, session_id, h.seq);

        if (is_batch == 0 && (h.flags & FLAG_LAST)) {
            uint32_t last = h.seq;
            for (int t = 0; t < ACK_RETRY_MAX; t++) {
                sl = sizeof(src);
                int dn = recvfrom(sock, recvbuf, PACKET_SIZE, 0, (struct sockaddr *)&src, &sl);
                if (dn <= 0) { send_ack(sock, dest, sendbuf, session_id, last); continue; }
                packet_header_t dh;
                if (read_header(recvbuf, dn, &dh) != 0) continue;
                if (dh.packet_type == PACKET_DONE) break;
                if (dh.packet_type == PACKET_DATA) { send_ack(sock, dest, sendbuf, session_id, dh.seq); t--; }
            }
            break;
        }
    }

    uint32_t result_crc = 0;
    if (is_batch == 0 && buf && recv_len > 0) {
        s_player_stop = true;                  /* a single frame replaces any animation */
        ESP_LOGI(TAG, "SINGLE FRAME: %u B, flag=0x%02X", (unsigned)recv_len, buf[0]);
        if (buf[0] == FRAME_FLAG_LZ4_PALETTE && render_lz4_palette(buf, recv_len) == 0) {
            ESP_LOGI(TAG, "RENDERED OK");
            result_crc = crc;
        }
    } else if (is_batch == 1 && batch_ok) {
        result_crc = crc;                      /* report batch CRC so next HELLO -> NOT_MODIFIED */
    }

    if (buf) heap_caps_free(buf);
    return result_crc;
}

/* ── main task ───────────────────────────────────────────────────────────── */

/* (Re)create the UDP socket with the HELLO recv/send timeouts applied.
 * Returns the socket fd, or -1 on failure. */
static int open_udp_socket(void)
{
    int s = socket(AF_INET, SOCK_DGRAM, IPPROTO_IP);
    if (s < 0) return -1;
    struct timeval tv = { .tv_sec = 3, .tv_usec = 0 };
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
    setsockopt(s, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof(tv));
    return s;
}

void udp_client_task(void *arg)
{
    (void)arg;

    frame_player_init();

    uint8_t token[16];
    resolve_token(token);

    int sock = open_udp_socket();
    if (sock < 0) { ESP_LOGE(TAG, "socket() failed errno=%d", errno); vTaskDelete(NULL); return; }

    struct sockaddr_in dest = {
        .sin_family = AF_INET, .sin_port = htons(SERVER_PORT),
        .sin_addr.s_addr = inet_addr(SERVER_IP),
    };

    static uint8_t sendbuf[PACKET_SIZE];
    static uint8_t recvbuf[PACKET_SIZE];

    /* Boot CRC: report the stored batch CRC so the first HELLO can earn NOT_MODIFIED
     * when flash frames are still valid (avoids re-downloading the same batch). */
    uint32_t current_frame_crc = 0;
    int hello_fails = 0;

    while (1) {
        /* Self-heal a wedged socket: reopen before sending if it was closed. */
        if (sock < 0) {
            sock = open_udp_socket();
            if (sock < 0) { ESP_LOGE(TAG, "socket recreate failed errno=%d", errno); vTaskDelay(pdMS_TO_TICKS(2000)); continue; }
            ESP_LOGI(TAG, "socket recreated");
        }

        send_hello(sock, &dest, sendbuf, token, current_frame_crc);

        struct sockaddr_in src;
        socklen_t src_len = sizeof(src);
        int rlen = recvfrom(sock, recvbuf, sizeof(recvbuf), 0, (struct sockaddr *)&src, &src_len);
        if (rlen <= 0) {
            ESP_LOGW(TAG, "HELLO: no response");
            if (++hello_fails >= HELLO_FAIL_MAX) {
                ESP_LOGW(TAG, "HELLO failed %dx - recreating socket", hello_fails);
                close(sock);
                sock = -1;
                hello_fails = 0;
            }
            vTaskDelay(pdMS_TO_TICKS(2000));
            continue;
        }
        hello_fails = 0;

        packet_header_t hdr;
        if (read_header(recvbuf, rlen, &hdr) != 0) { vTaskDelay(pdMS_TO_TICKS(2000)); continue; }

        if (hdr.packet_type == PACKET_NOT_MODIFIED) {
            ESP_LOGI(TAG, "NOT_MODIFIED (crc=0x%08X)", (unsigned)current_frame_crc);
            goto idle;
        }
        if (hdr.packet_type == PACKET_ERROR) {
            uint8_t code = (rlen > (int)sizeof(packet_header_t)) ? recvbuf[sizeof(packet_header_t)] : 0;
            ESP_LOGW(TAG, "AUTH rejected - error 0x%02X", code);
            vTaskDelay(pdMS_TO_TICKS(2000));
            continue;
        }
        if (hdr.packet_type != PACKET_AUTH || hdr.session_id == 0) continue;

        {
            uint16_t session_id = hdr.session_id;
            uint32_t total_size = hdr.seq;
            ESP_LOGI(TAG, "AUTH OK: session=%u total_size=%u B", session_id, (unsigned)total_size);

            uint32_t crc = receive_and_render(sock, &dest, sendbuf, recvbuf, session_id, total_size);
            if (crc != 0) current_frame_crc = crc;
        }

        idle:
        send_ping(sock, &dest, sendbuf, (uint32_t)(esp_timer_get_time() / 1000000));
        vTaskDelay(pdMS_TO_TICKS(3000));
    }
}
