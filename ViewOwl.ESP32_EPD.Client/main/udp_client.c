/**
 * @file udp_client.c
 * @brief ViewOwl e-paper client - UDP HELLO/AUTH (M2c).
 *
 * Speaks the shared packet.h protocol to the server: HELLO -> AUTH, then idles
 * with PING heartbeats. Frame receive + e-paper render come in M3. The socket
 * self-heals (recreate after HELLO_FAIL_MAX replies-less HELLOs) so a server that
 * was down at boot never leaves the client wedged.
 */
#include "udp_client.h"
#include "packet.h"
#include "config.h"
#include "nvs_storage.h"
#include "epd_4in2.h"

#include <errno.h>
#include <string.h>

#include "esp_log.h"
#include "esp_mac.h"
#include "esp_system.h"
#include "esp_timer.h"
#include "esp_rom_crc.h"

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/semphr.h"

#include "lwip/sockets.h"
#include "lwip/inet.h"

static const char *TAG = "udp_client";

/* ── token ──────────────────────────────────────────────────────────────── */

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

/* ── packet senders ─────────────────────────────────────────────────────── */

static void send_packet(int sock, const struct sockaddr_in *dest, uint8_t *buf,
                        uint8_t type, uint16_t session_id, uint32_t seq,
                        uint16_t flags, const uint8_t *payload, size_t payload_len)
{
    packet_header_t hdr = {
        .magic = MAGIC_TOKEN, .packet_type = type, .session_id = session_id,
        .seq = seq, .flags = flags,
    };
    int len = write_packet(buf, &hdr, payload, payload_len);
    if (len > 0) {
        sendto(sock, buf, len, 0, (const struct sockaddr *)dest, sizeof(*dest));
    }
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

static void send_ping(int sock, const struct sockaddr_in *dest, uint8_t *buf,
                      uint32_t uptime_s)
{
    ping_payload_t pp = {
        .uptime = uptime_s, .free_heap = (uint32_t)esp_get_free_heap_size(),
        .wifi_rssi = -50, .reserved = 0,
    };
    send_packet(sock, dest, buf, PACKET_PING, 0, 0, 0, (const uint8_t *)&pp, sizeof(pp));
}

static void send_ack(int sock, const struct sockaddr_in *dest, uint8_t *buf,
                     uint16_t session_id, uint32_t seq)
{
    send_packet(sock, dest, buf, PACKET_ACK, session_id, seq, 0, NULL, 0);
}

/* ── frame receive + render ───────────────────────────────────────────────── */

/* Invert the 1-bit frame before display: dark-themed templates render as black-on-e-ink;
 * inverting gives the natural white-paper look. Blanket stopgap — becomes a per-template
 * light/dark flag (server-side) with template tagging. */
#define EPD_INVERT 1

/* Frame buffer — holds either a 1-bit mono (15000 B) or a 2-bit 4-gray (30000 B)
 * frame. Static, off-stack. */
static uint8_t s_frame[EPD_BUF_SIZE_4GRAY + 16];

/* ── Class-C playback: RAM frame store + player task ──────────────────────────
 * Class-C animations arrive as a BATCH of raw 1-bit frames (mono = 0x00 flag +
 * EPD_BUF_SIZE bytes each). They are held in RAM (frame counts are small here,
 * no flash needed) and looped by a player task that whole-screen partial-refreshes
 * each frame — fast playback with a periodic full refresh to clear ghosting. */
#define RAM_BATCH_CAP     (9 * (EPD_BUF_SIZE + 8))  /* up to ~9 mono frames (~135 KB) */
#define PLAYER_FULL_EVERY 0                          /* periodic full refresh every N partials; 0 = OFF (partial-only). e-ink refresh draws current spikes — a weak USB supply can brown out the waveform; the epd_init-clean-full path stays ready if re-enabled (>0). */

static uint8_t  s_batch[RAM_BATCH_CAP];
static uint32_t s_frame_off[BATCH_MAX_FRAMES];
static uint32_t s_frame_len[BATCH_MAX_FRAMES];
static uint8_t  s_frame_count = 0;
static uint8_t  s_frame_fps   = 4;
static uint32_t s_write_pos   = 0;
static volatile bool s_batch_valid = false;

static uint8_t s_play[EPD_BUF_SIZE];                 /* per-frame inverted scratch */

static SemaphoreHandle_t s_player_sem  = NULL;
static volatile bool     s_player_stop = false;

/* Prepare the RAM store for a new batch: record per-frame offsets + sizes. */
static esp_err_t ram_frames_write_begin(uint8_t count, uint8_t fps, const uint32_t *sizes)
{
    if (count == 0 || count > BATCH_MAX_FRAMES) return ESP_ERR_INVALID_ARG;
    uint32_t off = 0;
    for (int i = 0; i < count; i++) {
        s_frame_off[i] = off;
        s_frame_len[i] = sizes[i];
        off += sizes[i];
    }
    if (off > sizeof(s_batch)) {
        ESP_LOGE(TAG, "batch %u B > RAM cap %u B", (unsigned)off, (unsigned)sizeof(s_batch));
        return ESP_ERR_NO_MEM;
    }
    s_frame_count = count;
    s_frame_fps   = (fps > 0) ? fps : 4;
    s_write_pos   = 0;
    s_batch_valid = false;
    return ESP_OK;
}

/* Append a received DATA chunk to the batch buffer (frames are concatenated). */
static void ram_frames_write_chunk(const uint8_t *data, uint16_t len)
{
    if (s_write_pos + len <= sizeof(s_batch)) {
        memcpy(s_batch + s_write_pos, data, len);
        s_write_pos += len;
    }
}

static esp_err_t ram_frames_write_commit(void)
{
    s_batch_valid = (s_write_pos > 0);
    return s_batch_valid ? ESP_OK : ESP_FAIL;
}

/* Render one stored mono frame (raw: 0x00 flag + EPD_BUF_SIZE bytes): strip the
 * flag, invert to the white-paper look, then blit. full = full refresh, else partial. */
static void player_blit(uint8_t idx, bool full)
{
    uint32_t flen = s_frame_len[idx];
    if (flen != EPD_BUF_SIZE && flen != EPD_BUF_SIZE + 1) return;
    const uint8_t *f    = s_batch + s_frame_off[idx];
    const uint8_t *data = (flen == EPD_BUF_SIZE + 1) ? f + 1 : f;   /* skip 0x00 flag */
    for (uint32_t i = 0; i < EPD_BUF_SIZE; i++) {
        s_play[i] = (uint8_t)(EPD_INVERT ? (data[i] ^ 0xFF) : data[i]);
    }
    if (full) epd_display(s_play);
    else      epd_display_part(0, 0, EPD_W, EPD_H, s_play);
}

/* Loop the stored batch via partial refresh until s_player_stop is raised. */
static void player_task_fn(void *arg)
{
    (void)arg;
    while (1) {
        xSemaphoreTake(s_player_sem, portMAX_DELAY);
        s_player_stop = false;
        if (!s_batch_valid || s_frame_count == 0) continue;

        uint32_t delay_ms = 1000u / s_frame_fps;
        ESP_LOGI(TAG, "player: %u frames @ %u fps (%u ms/frame)",
                 s_frame_count, s_frame_fps, (unsigned)delay_ms);

        epd_init();
        player_blit(0, true);                        /* full-refresh baseline */
        uint8_t idx = (s_frame_count > 1) ? 1 : 0;
        int since_full = 0;
        while (!s_player_stop) {
            bool full = (PLAYER_FULL_EVERY > 0) && (++since_full >= PLAYER_FULL_EVERY);
            if (full) {
                since_full = 0;
                epd_init();   /* reset out of partial mode so the full refresh is clean (no lingering ghost) */
            }
            player_blit(idx, full);
            idx = (uint8_t)((idx + 1) % s_frame_count);
            vTaskDelay(pdMS_TO_TICKS(delay_ms));
        }
        ESP_LOGI(TAG, "player: stopped");
    }
}

/* Create the player task (idles until a committed batch gives the semaphore). */
void frame_player_init(void)
{
    s_player_sem = xSemaphoreCreateBinary();
    configASSERT(s_player_sem);
    xTaskCreate(player_task_fn, "frame_player", 4096, NULL, 4, NULL);
}

/* Receive a single-frame transfer (Class A/B) or a Class-C batch, then render/play.
 *   is_batch == -1: undecided — the first packet (BATCH_START or DATA) decides.
 *   is_batch ==  0: single frame into s_frame, rendered here.
 *   is_batch ==  1: frames streamed into the RAM store, then the player loops them.
 * Returns the CRC over the received payload (for the next HELLO's NOT_MODIFIED) or 0. */
static uint32_t receive_and_render(int sock, const struct sockaddr_in *dest,
                                   uint8_t *sendbuf, uint8_t *recvbuf,
                                   uint16_t session_id, uint32_t total_size)
{
    uint32_t recv_len = 0, crc = 0, expected = 0;
    int ack_to = 0, is_batch = -1;
    struct sockaddr_in src;
    socklen_t sl;

    while (1)
    {
        sl = sizeof(src);
        int n = recvfrom(sock, recvbuf, PACKET_SIZE, 0, (struct sockaddr *)&src, &sl);
        if (n <= 0)
        {
            if (ack_to++ < ACK_RETRY_MAX && expected > 0)
            {
                send_ack(sock, dest, sendbuf, session_id, expected - 1);
                continue;
            }
            ESP_LOGW(TAG, "frame transfer stalled (seq=%u)", (unsigned)expected);
            break;
        }
        ack_to = 0;

        packet_header_t h;
        if (read_header(recvbuf, n, &h) != 0) break;
        if (h.session_id != session_id) continue;

        /* First post-AUTH packet decides single-frame vs batch. */
        if (is_batch == -1)
        {
            if (h.packet_type == PACKET_BATCH_START)
            {
                if (h.payload_length < (uint16_t)sizeof(batch_start_payload_t)) break;
                batch_start_payload_t bsp;
                memcpy(&bsp, recvbuf + sizeof(packet_header_t), sizeof(bsp));
                ESP_LOGI(TAG, "BATCH_START: %u frames @ %u fps", bsp.frame_count, bsp.fps);
                s_player_stop = true;                  /* stop playback before overwriting RAM */
                vTaskDelay(pdMS_TO_TICKS(150));
                if (ram_frames_write_begin(bsp.frame_count, bsp.fps, bsp.frame_sizes) != ESP_OK)
                {
                    send_ack(sock, dest, sendbuf, session_id, 0);
                    break;
                }
                is_batch = 1;
                expected = 0;
                send_ack(sock, dest, sendbuf, session_id, 0);
                continue;
            }
            if (h.packet_type == PACKET_DATA)
            {
                if (total_size == 0 || total_size > sizeof(s_frame))
                {
                    ESP_LOGW(TAG, "bad single-frame total_size %u", (unsigned)total_size);
                    break;
                }
                is_batch = 0;                          /* fall through to process this DATA */
            }
            else break;
        }

        if (is_batch == 1 && h.packet_type == PACKET_BATCH_COMMIT)
        {
            esp_err_t e = ram_frames_write_commit();
            send_ack(sock, dest, sendbuf, session_id, 0);
            if (e == ESP_OK)
            {
                ESP_LOGI(TAG, "BATCH committed (%u B) — starting playback", (unsigned)s_write_pos);
                s_player_stop = false;
                xSemaphoreGive(s_player_sem);
            }
            break;
        }

        if (h.packet_type != PACKET_DATA) break;

        if (h.seq < expected) { send_ack(sock, dest, sendbuf, session_id, h.seq); continue; }
        if (h.seq > expected) { if (expected > 0) send_ack(sock, dest, sendbuf, session_id, expected - 1); continue; }

        const uint8_t *pl = recvbuf + sizeof(packet_header_t);
        uint16_t pl_len = h.payload_length;
        if (is_batch == 1)
        {
            ram_frames_write_chunk(pl, pl_len);
        }
        else if (recv_len + pl_len <= sizeof(s_frame))
        {
            memcpy(s_frame + recv_len, pl, pl_len);
            recv_len += pl_len;
        }
        crc = esp_rom_crc32_le(crc, pl, pl_len);
        expected++;
        send_ack(sock, dest, sendbuf, session_id, h.seq);

        if (is_batch == 0 && (h.flags & FLAG_LAST))
        {
            uint32_t last = h.seq;
            for (int t = 0; t < ACK_RETRY_MAX; t++)
            {
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

    /* Batch path: the player task renders; just report the CRC for NOT_MODIFIED. */
    if (is_batch == 1) return crc;

    if (recv_len == 0) return 0;
    ESP_LOGI(TAG, "FRAME received: %u B (byte0=0x%02X)", (unsigned)recv_len, s_frame[0]);

    /* Frames carry an optional 1-byte format-flag prefix + raw payload:
     * mono = (1+)EPD_BUF_SIZE B, 4-gray = (1+)EPD_BUF_SIZE_4GRAY B. Detect by size and
     * skip the flag byte if present (the server prepends it). */
    const uint8_t *img;
    uint32_t data_len;
    if (recv_len == EPD_BUF_SIZE || recv_len == EPD_BUF_SIZE + 1)
    {
        img      = s_frame + (recv_len - EPD_BUF_SIZE);
        data_len = EPD_BUF_SIZE;
    }
    else if (recv_len == EPD_BUF_SIZE_4GRAY || recv_len == EPD_BUF_SIZE_4GRAY + 1)
    {
        img      = s_frame + (recv_len - EPD_BUF_SIZE_4GRAY);
        data_len = EPD_BUF_SIZE_4GRAY;
    }
    else
    {
        ESP_LOGW(TAG, "frame size %u != mono %u(+1) / 4-gray %u(+1) — not rendering",
                 (unsigned)recv_len, (unsigned)EPD_BUF_SIZE, (unsigned)EPD_BUF_SIZE_4GRAY);
        return crc;
    }

#if EPD_INVERT
    /* XOR 0xFF inverts both formats: 1-bit (white<->black) and 2-bit packed codes
     * (11<->00 white/black, 10<->01 light/dark grey). */
    for (uint32_t i = 0; i < data_len; i++) ((uint8_t *)img)[i] ^= 0xFF;
#endif

    if (data_len == EPD_BUF_SIZE)
    {
        epd_init();
        epd_display(img);
        ESP_LOGI(TAG, "RENDERED mono 1-bit (full refresh, inverted=%d)", EPD_INVERT);
    }
    else
    {
        epd_init_4gray();
        epd_display_4gray(img);
        ESP_LOGI(TAG, "RENDERED 4-gray mono-CGA (inverted=%d)", EPD_INVERT);
    }
    return crc;
}

/* ── socket ─────────────────────────────────────────────────────────────── */

/* (Re)create the UDP socket with the HELLO recv/send timeouts. Returns fd or -1. */
static int open_udp_socket(void)
{
    int s = socket(AF_INET, SOCK_DGRAM, IPPROTO_IP);
    if (s < 0) return -1;
    struct timeval tv = { .tv_sec = 3, .tv_usec = 0 };
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
    setsockopt(s, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof(tv));
    return s;
}

/* ── main task ──────────────────────────────────────────────────────────── */

void udp_client_task(void *arg)
{
    (void)arg;

    uint8_t token[16];
    resolve_token(token);

    int sock = open_udp_socket();
    if (sock < 0) {
        ESP_LOGE(TAG, "socket() failed errno=%d", errno);
        vTaskDelete(NULL);
        return;
    }

    struct sockaddr_in dest = {
        .sin_family = AF_INET, .sin_port = htons(SERVER_PORT),
        .sin_addr.s_addr = inet_addr(SERVER_IP),
    };

    static uint8_t sendbuf[PACKET_SIZE];
    static uint8_t recvbuf[PACKET_SIZE];

    uint32_t current_frame_crc = 0;   /* 0 until M3 stores a frame -> earns NOT_MODIFIED */
    int hello_fails = 0;

    while (1) {
        if (sock < 0) {
            sock = open_udp_socket();
            if (sock < 0) {
                ESP_LOGE(TAG, "socket recreate failed errno=%d", errno);
                vTaskDelay(pdMS_TO_TICKS(2000));
                continue;
            }
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
        if (read_header(recvbuf, rlen, &hdr) != 0) {
            vTaskDelay(pdMS_TO_TICKS(2000));
            continue;
        }

        if (hdr.packet_type == PACKET_NOT_MODIFIED) {
            ESP_LOGI(TAG, "NOT_MODIFIED (crc=0x%08X)", (unsigned)current_frame_crc);
            goto idle;
        }
        if (hdr.packet_type == PACKET_ERROR) {
            uint8_t code = (rlen > (int)sizeof(packet_header_t)) ? recvbuf[sizeof(packet_header_t)] : 0;
            ESP_LOGW(TAG, "AUTH rejected - error 0x%02X (is the token registered?)", code);
            vTaskDelay(pdMS_TO_TICKS(3000));
            continue;
        }
        if (hdr.packet_type != PACKET_AUTH || hdr.session_id == 0) {
            continue;
        }

        {
            uint16_t session_id = hdr.session_id;
            uint32_t total_size = hdr.seq;
            ESP_LOGI(TAG, "AUTH OK: session=%u, total_size=%u B", session_id, (unsigned)total_size);
            uint32_t crc = receive_and_render(sock, &dest, sendbuf, recvbuf, session_id, total_size);
            if (crc != 0) current_frame_crc = crc;
        }

        idle:
        send_ping(sock, &dest, sendbuf, (uint32_t)(esp_timer_get_time() / 1000000));
        vTaskDelay(pdMS_TO_TICKS(3000));
    }
}
