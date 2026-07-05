/* UDP client — HELLO/AUTH/DATA/ACK/DONE protocol, batch flash write, NVS CRC persistence.
 * See: docs/architecture.md — "ESP32 Firmware" and "Packet protocol"
 * See: docs/hardware.md    — "UDP packet sizing" and "Flash partition / large frame storage" */
#include "udp_client.h"
#include "frame_buffer.h"
#include "lcd_render.h"
#include "udp_protocol.h"
#include "frame_decoder.h"
#include "flash_frames.h"
#include "lcd_log.h"
#include "nvs_storage.h"
#include "config.h"
#include "packet.h"

#include <errno.h>
#include <string.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

#include "esp_log.h"
#include "esp_system.h"
#include "esp_timer.h"
#include "esp_rom_crc.h"
#include "esp_heap_caps.h"

static const char *TAG = "udp_client";

/* Shared with main.c WDT watchdog: updated after every successful frame render. */
int64_t last_success_for_WDT = 0;

/* Performance counter: print timing every N frames. */
#define TIME_CYCLE_COUNT 10

/* ── Class-C frame player state ───────────────────────────────────────── */

/* Binary semaphore: given after BATCH_COMMIT to start playback;
 * taken by player_task to wait for each new batch. */
static SemaphoreHandle_t s_player_start_sem = NULL;

/* Set to true before a new BATCH transfer begins so the player stops. */
static volatile bool s_player_stop_requested = false;

/**
 * @brief FreeRTOS task: loops through stored frames from the flash partition.
 *
 * Waits on s_player_start_sem after boot (or after each batch transfer)
 * and plays back the frames at the configured FPS until
 * s_player_stop_requested is set by the main UDP task.
 */
static void player_task_fn(void *arg)
{
    /* Buffer is allocated lazily after the semaphore fires so that the
     * frame_buffer recv buffer (up to 106 KB on no-PSRAM devices) can be
     * freed by the main task before we attempt allocation.  This avoids an
     * "out of memory" failure on devices without PSRAM. */
    uint32_t player_buf_cap = 0;
    uint8_t *frame_buf      = NULL;

    while (1) {
        /* Wait until a committed batch is ready. */
        xSemaphoreTake(s_player_start_sem, portMAX_DELAY);
        s_player_stop_requested = false;

        /* Allocate (or re-use) the playback buffer now that the recv buffer
         * has been freed by the main task after BATCH_COMMIT. */
        if (!frame_buf) {
            frame_buf = heap_caps_malloc(IMAGE_SIZE + 1u, MALLOC_CAP_SPIRAM);
            if (frame_buf) {
                player_buf_cap = IMAGE_SIZE + 1u;
            } else {
                /* No PSRAM — try progressively smaller DRAM blocks. */
                static const uint32_t k_player_dram_sizes[] = {
                    96u * 1024u,
                    64u * 1024u,
                    48u * 1024u,
                    32u * 1024u,
                    24u * 1024u,
                    16u * 1024u,
                };
                for (size_t i = 0;
                     i < sizeof(k_player_dram_sizes) / sizeof(k_player_dram_sizes[0]);
                     i++) {
                    frame_buf = heap_caps_malloc(k_player_dram_sizes[i],
                                                 MALLOC_CAP_8BIT | MALLOC_CAP_INTERNAL);
                    if (frame_buf) {
                        player_buf_cap = k_player_dram_sizes[i];
                        ESP_LOGW(TAG, "player: no PSRAM — DRAM buf %u B",
                                 (unsigned)player_buf_cap);
                        break;
                    }
                }
            }

            if (!frame_buf) {
                ESP_LOGE(TAG, "player: cannot allocate frame buffer — skipping batch");
                continue;
            }
        }

        if (!flash_frames_valid()) {
            ESP_LOGW(TAG, "player: semaphore given but partition invalid");
            continue;
        }

        uint8_t  count    = flash_frames_count();
        uint8_t  fps      = flash_frames_fps();
        uint32_t delay_ms = (fps > 0u) ? (1000u / fps) : 100u;
        uint32_t max_sz   = flash_frames_max_frame_size();

        ESP_LOGI(TAG, "player: starting — %u frames @ %u fps (%u ms/frame) max_frame=%u B buf=%u B",
                 count, fps, (unsigned)delay_ms, (unsigned)max_sz, (unsigned)player_buf_cap);

        /* Abort playback if even the largest frame exceeds our buffer. */
        if (max_sz > player_buf_cap) {
            ESP_LOGE(TAG, "player: max frame size %u B > buf %u B — cannot play",
                     (unsigned)max_sz, (unsigned)player_buf_cap);
            continue;
        }

        /* Suppress all TUI output for the duration of playback so no text
         * overlays corrupt the animated frame content on screen. */
        lcd_log_set_player_active(true);
        /* Latch has_frame as well: player_active is cleared before every BATCH
         * transfer, and a transfer that dies mid-way (Wi-Fi loss) leaves it
         * cleared.  Without this latch both suppression flags end up false and
         * every retry log line paints the full TUI over the remnants of the
         * last frame.  has_frame stays true for the lifetime of the device, so
         * after the first successful playback only the no-server badge may
         * draw over content — never the scrolling log. */
        lcd_log_set_has_frame(true);

        uint8_t idx = 0;

        uint32_t frames_rendered = 0;

        while (!s_player_stop_requested) {
            size_t    frame_len = 0;
            int64_t   t_frame0  = esp_timer_get_time();
            esp_err_t err = flash_frames_read(idx, frame_buf, player_buf_cap, &frame_len);

            if (err != ESP_OK) {
                ESP_LOGE(TAG, "player: read frame %u failed: %s", idx, esp_err_to_name(err));
            } else if (frame_buf[0] == FRAME_FLAG_LZ4_PALETTE) {
                int rc = lcd_render_lz4(frame_buf, frame_len);
                if (rc == 0) {
                    last_success_for_WDT = esp_timer_get_time();
                    frames_rendered++;
                    if (frames_rendered == 1) {
                        ESP_LOGI(TAG, "player: first frame rendered OK (lz4 flag=0x%02X len=%u)",
                                 frame_buf[0], (unsigned)frame_len);
                    }
                } else {
                    ESP_LOGE(TAG, "player: lcd_render_lz4 failed rc=%d frame=%u len=%u",
                             rc, idx, (unsigned)frame_len);
                }
            } else if (frame_buf[0] == FRAME_FLAG_PALETTE) {
                int rc = lcd_render_palette(frame_buf, frame_len);
                if (rc == 0) {
                    last_success_for_WDT = esp_timer_get_time();
                    frames_rendered++;
                    if (frames_rendered == 1) {
                        ESP_LOGI(TAG, "player: first frame rendered OK (pal flag=0x%02X len=%u)",
                                 frame_buf[0], (unsigned)frame_len);
                    }
                } else {
                    ESP_LOGE(TAG, "player: lcd_render_palette failed rc=%d frame=%u len=%u",
                             rc, idx, (unsigned)frame_len);
                }
            } else {
                ESP_LOGW(TAG, "player: unsupported frame flag 0x%02X at idx %u len=%u",
                         frame_buf[0], idx, (unsigned)frame_len);
            }

#if PLAYER_FREE_RUN
            /* Free-run: play as fast as the decode + SPI path allows.  One-tick
             * yield keeps the idle task / Wi-Fi stack serviced (task WDT). */
            (void)t_frame0;
            idx = (uint8_t)((idx + 1u) % count);
            vTaskDelay(1);
#else
            /* Paced playback: honour the template's encoded fps, but subtract
             * this frame's actual work (read + render) from the target interval
             * so the displayed rate tracks the encoded fps instead of
             * (work + delay).  When the hardware cannot keep up (work >= the
             * target interval) the sleep floors at one tick, so the device
             * free-runs at its render-bound maximum.  The 1-tick floor also
             * guarantees the idle task / Wi-Fi stack are serviced (task WDT). */
            uint32_t work_ms = (uint32_t)((esp_timer_get_time() - t_frame0) / 1000);
            TickType_t sleep_ticks =
                (delay_ms > work_ms) ? pdMS_TO_TICKS(delay_ms - work_ms) : 0;
            if (sleep_ticks == 0) {
                sleep_ticks = 1;
            }
            idx = (uint8_t)((idx + 1u) % count);
            vTaskDelay(sleep_ticks);
#endif
        }

        ESP_LOGI(TAG, "player: stopped (new batch incoming)");
    }
}

/**
 * @brief Creates the player semaphore and spawns the player FreeRTOS task.
 *
 * Must be called once from udp_client_task before the main loop.
 * If the flash partition already holds a valid frame set from a previous
 * boot, playback is started immediately.
 */
static void frame_player_init(void)
{
    s_player_start_sem = xSemaphoreCreateBinary();
    configASSERT(s_player_start_sem);

    xTaskCreate(player_task_fn, "frame_player", 6144, NULL, 4, NULL);

    /* If frames were stored in a previous session, start playing immediately.
     * Free the recv buffer first so the player task can claim that DRAM on
     * no-PSRAM devices — otherwise the player fails to allocate on the first
     * semaphore and the screen stays dark until the next BATCH_COMMIT. */
    if (flash_frames_valid()) {
        ESP_LOGI(TAG, "frame_player_init: valid frames in flash — resuming playback");
        frame_buffer_free_recv();
        xSemaphoreGive(s_player_start_sem);
    }
}

/**
 * @brief UDP client task.
 *
 * Lifecycle per iteration:
 *   1. Send HELLO (token + display info + last frame CRC).
 *   2. Wait for AUTH → receive session id and total frame size.
 *   3. Receive DATA chunks → ACK each → save to flash or DRAM/PSRAM buffer.
 *   4. On FLAG_LAST → wait for DONE (with ACK retry) → render to LCD.
 *   5. Idle for IDLE_DURATION_S, sending PING heartbeats and handling CONFIG.
 *   6. Repeat.
 *
 * @param pvParameters Unused (required by FreeRTOS task signature).
 */
void udp_client_task(void *pvParameters)
{
    frame_buffer_init();
    frame_player_init();

    int sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_IP);
    if (sock < 0) {
        ESP_LOGE(TAG, "Cannot create socket: errno %d", errno);
        lcd_log_error("UDP: cannot create socket — halted");
        vTaskDelete(NULL);
        return;
    }

    struct sockaddr_in dest_addr = {0};
    dest_addr.sin_addr.s_addr = inet_addr(SERVER_IP);
    dest_addr.sin_family      = AF_INET;
    dest_addr.sin_port        = htons(SERVER_PORT);

    /* 3-second receive timeout so the idle PING loop can run. */
    struct timeval tv = { .tv_sec = 3, .tv_usec = 0 };
    setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
    setsockopt(sock, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof(tv));

    uint8_t sendbuf[PACKET_SIZE];
    uint8_t recvbuf[PACKET_SIZE];

    /* CRC32 of the last successfully rendered frame (0 = no frame yet).
     * Sent in every HELLO so the server can skip unchanged frames.
     * Restored from NVS on boot so the first HELLO after a reboot carries
     * the real CRC — enabling the server to send NOT_MODIFIED instead of
     * triggering a full BATCH re-download when the flash frames are current. */
    uint32_t current_frame_crc = 0;
    {
        /* Only restore the NVS-persisted CRC when the flash partition still holds
         * valid frames.  If the frames partition was erased (e.g. full USB re-flash)
         * a stale NVS CRC would make the server reply NOT_MODIFIED on every HELLO
         * while the device has nothing to play — animation would never start.
         * Sending CRC=0 instead forces a fresh BATCH download to refill the partition.
         * Also clear the NVS entry in that case so subsequent boots behave the same. */
        if (flash_frames_valid()) {
            uint32_t saved_crc = 0;
            if (nvs_storage_read_batch_crc(&saved_crc) == ESP_OK && saved_crc != 0) {
                current_frame_crc = saved_crc;
                ESP_LOGI(TAG, "boot: restored batch CRC 0x%08X from NVS", (unsigned)saved_crc);
            }
        } else {
            /* Flash partition empty — stale NVS CRC would be misleading; reset it. */
            nvs_storage_write_batch_crc(0);
            ESP_LOGI(TAG, "boot: flash frames absent — NVS batch CRC cleared, will force BATCH");
        }
    }

    /* After a successful BATCH_COMMIT the player runs independently from flash.
     * Stay in idle for BATCH_IDLE_S (5 min) instead of 1 s so the player can
     * complete many animation cycles before the next HELLO.
     * The server's PushLoop sends an AUTH trigger when new content is grabbed,
     * breaking idle early — so the 5-min cap is only a safety-net fallback.
     * Also set on boot when valid frames already exist in flash so that a cold
     * reboot with stored frames does not cause constant 1-second HELLO churn. */
    bool     after_batch_commit = flash_frames_valid();

    /* Set when a BATCH transfer exits without COMMIT (timeout, sequence error, etc.).
     * Causes the idle section to use BATCH_RETRY_DELAY_S instead of IDLE_DURATION_S,
     * preventing a tight 1-second retry loop that would hammer the server.
     * The early-AUTH push mechanism can still break the idle immediately when the
     * server has a fresh batch ready. */
    bool     batch_failed = false;

    /* Counts consecutive NOT_MODIFIED responses received while in single-frame mode
     * (after_batch_commit == false).  When it reaches BATCH_IDLE_S (~5 min) the CRC
     * is reset to 0 so the next HELLO forces the server to re-evaluate.  If a batch
     * grab has been completed in the meantime (Class-C template) the device receives
     * BATCH_START and animation resumes.  Resets to 0 on any successful frame render
     * or BATCH_COMMIT so the countdown only accumulates during a genuine stall. */
    int      not_modified_no_batch_count = 0;

    /* Timestamp of the last successful HELLO exchange (non-zero after first reply).
     * Used to trigger the Type-A/B "no server" indicator after 30 s of silence. */
    int64_t  last_server_ok_us  = 0;

    int     frame_count      = 0;
    int     hello_fail_count = 0;
    int64_t perf_start       = esp_timer_get_time();

    /* Fire an immediate PING on the very first idle period after boot so the
     * server receives rssi / uptime / heap without waiting PING_INTERVAL_S seconds. */
    static bool s_first_ping_done = false;

    while (1) {
        /* Restore 3-second timeout — the idle period adjusts SO_RCVTIMEO
         * dynamically; reset it here before each HELLO attempt. */
        struct timeval tv_3s = { .tv_sec = 3, .tv_usec = 0 };
        setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, &tv_3s, sizeof(tv_3s));

        /* ── 1. HELLO ──────────────────────────────────────────────────── */
        udp_protocol_send_hello(sock, &dest_addr, sendbuf, current_frame_crc);

        struct sockaddr_in src_addr;
        socklen_t          src_len = sizeof(src_addr);

        int rlen = recvfrom(sock, recvbuf, sizeof(recvbuf), 0,
                            (struct sockaddr *)&src_addr, &src_len);
        if (rlen <= 0) {
            /* Timeout or socket error — track consecutive failures. */
            hello_fail_count++;
            ESP_LOGW(TAG, "HELLO: no response (%d/%d)", hello_fail_count, HELLO_FAIL_MAX);
            if (hello_fail_count >= HELLO_FAIL_MAX) {
                /* Socket may be stuck — close and reopen to recover. */
                ESP_LOGW(TAG, "HELLO: socket unresponsive after %d attempts — recreating UDP socket",
                         HELLO_FAIL_MAX);
                close(sock);
                sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_IP);
                if (sock < 0) {
                    ESP_LOGE(TAG, "Socket recreate failed errno=%d — rebooting", errno);
                    esp_restart();
                }
                struct timeval tv_reset = { .tv_sec = 3, .tv_usec = 0 };
                setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, &tv_reset, sizeof(tv_reset));
                setsockopt(sock, SOL_SOCKET, SO_SNDTIMEO, &tv_reset, sizeof(tv_reset));
                hello_fail_count = 0;
                ESP_LOGI(TAG, "HELLO: socket recreated successfully");
            }
            /* Show a dim "NO SERVER" hint for Type A/B frames after 30 s of silence.
             * Type C (player active) suppresses this automatically inside lcd_log. */
            if (last_server_ok_us != 0 &&
                (esp_timer_get_time() - last_server_ok_us) > 30LL * 1000000LL) {
                lcd_log_show_no_server();
            }
            vTaskDelay(pdMS_TO_TICKS(2000));
            continue;
        }

        /* Any response means the socket is alive — feed the 10-min WDT.
         * Covers ERR_NO_FRAME, ERR_NO_TEMPLATE, NOT_MODIFIED, AUTH OK, etc. */
        hello_fail_count = 0;
        last_success_for_WDT = esp_timer_get_time();
        last_server_ok_us    = last_success_for_WDT;

        /* ── 2. AUTH (or ERROR / NOT_MODIFIED) ────────────────────────── */
        packet_header_t hdr;
        if (read_header(recvbuf, rlen, &hdr) != 0) {
            ESP_LOGW(TAG, "Bad packet after HELLO (no valid header)");
            vTaskDelay(pdMS_TO_TICKS(2000));
            continue;
        }

        if (hdr.packet_type == PACKET_NOT_MODIFIED) {
            /* Server confirmed the frame has not changed — skip transfer. */
            lcd_log_hide_wait();
            ESP_LOGI(TAG, "NOT_MODIFIED: frame unchanged (crc=0x%08X) — skipping download",
                     (unsigned)current_frame_crc);

            /* Track consecutive NOT_MODIFIED responses while in single-frame mode.
             * After BATCH_IDLE_S cycles (~5 min) reset the CRC to 0 so the next
             * HELLO forces the server to re-evaluate the frame.  For Class-C
             * templates this allows the device to pick up a freshly-grabbed batch
             * without waiting for an explicit AUTH trigger — self-healing when the
             * DeviceTemplateRefreshWorker finishes its next cycle.
             * For Class-A/B templates the cost is one unnecessary re-download per
             * 5 min — acceptable. */
            if (!after_batch_commit) {
                if (++not_modified_no_batch_count >= BATCH_IDLE_S) {
                    current_frame_crc           = 0;
                    not_modified_no_batch_count = 0;
                    ESP_LOGW(TAG,
                        "NOT_MODIFIED: stuck in single-frame mode for %d cycles "
                        "— resetting CRC to force server re-check",
                        BATCH_IDLE_S);
                }
            } else {
                not_modified_no_batch_count = 0;
            }

        } else if (hdr.packet_type == PACKET_ERROR) {
            uint8_t err = (rlen > (int)sizeof(packet_header_t))
                ? recvbuf[sizeof(packet_header_t)] : 0;
            switch (err) {
                case ERR_BAD_TOKEN:
                    ESP_LOGE(TAG, "AUTH rejected: token format unrecognised by server");
                    lcd_log_show_wait("TOKEN ERROR",
                                      "Token not recognised",
                                      "Check TOKEN in config.h");
                    for (int w = 0; w < 15; w++) {
                        vTaskDelay(pdMS_TO_TICKS(200));
                        lcd_log_spin();
                    }
                    continue;
                case ERR_UNKNOWN_DEVICE:
                    ESP_LOGE(TAG, "AUTH rejected: device token not registered in database");
                    lcd_log_show_wait("UNKNOWN DEVICE",
                                      "Device not registered",
                                      "Add device in dashboard");
                    for (int w = 0; w < 15; w++) {
                        vTaskDelay(pdMS_TO_TICKS(200));
                        lcd_log_spin();
                    }
                    continue;
                case ERR_NO_TEMPLATE:
                    ESP_LOGE(TAG, "AUTH rejected: device has no active template assigned");
                    lcd_log_show_wait("NO TEMPLATE",
                                      "No template assigned",
                                      "Assign one in dashboard");
                    for (int w = 0; w < 15; w++) {
                        vTaskDelay(pdMS_TO_TICKS(200));
                        lcd_log_spin();
                    }
                    continue;
                case ERR_NO_FRAME:
                    ESP_LOGE(TAG, "AUTH rejected: template assigned but no frame captured yet");
                    lcd_log_show_wait("NO FRAME YET",
                                      "Waiting for grabber to",
                                      "capture the first frame");
                    for (int w = 0; w < 15; w++) {
                        vTaskDelay(pdMS_TO_TICKS(200));
                        lcd_log_spin();
                    }
                    continue;
                default:
                    ESP_LOGE(TAG, "AUTH rejected: server error code 0x%02X", err);
                    lcd_log_error("AUTH: server rejected (unknown error)");
                    break;
            }
            vTaskDelay(pdMS_TO_TICKS(3000));
            continue;

        } else if (hdr.packet_type == PACKET_AUTH && hdr.session_id == 0) {
            /* Wake-up AUTH trigger (SessionId=0): signals a new frame is ready.
             * Ignore it here; the real AUTH (SessionId > 0) will arrive shortly. */
            ESP_LOGD(TAG, "Push AUTH trigger during HELLO-wait — ignoring, waiting for real AUTH");
            continue;

        } else if (hdr.packet_type != PACKET_AUTH) {
            ESP_LOGW(TAG, "Expected AUTH, got packet type 0x%X — retrying", hdr.packet_type);
            vTaskDelay(pdMS_TO_TICKS(2000));
            continue;

        } else {
            /* ── 3. AUTH OK → check for BATCH_START or regular DATA ─────── */
            lcd_log_hide_wait();
            uint16_t session_id = hdr.session_id;

            /* AUTH.seq carries the total frame size — used to select flash vs DRAM path. */
            uint32_t total_size = hdr.seq;
            ESP_LOGI(TAG, "AUTH OK: session=%u total_size=%u B", session_id, (unsigned)total_size);

            /* Peek at the first packet after AUTH to detect Class-C batch transfer. */
            rlen = recvfrom(sock, recvbuf, sizeof(recvbuf), 0,
                            (struct sockaddr *)&src_addr, &src_len);
            if (rlen <= 0) {
                ESP_LOGW(TAG, "Timeout after AUTH — retrying");
                continue;
            }
            if (read_header(recvbuf, rlen, &hdr) != 0) {
                ESP_LOGW(TAG, "Bad packet after AUTH — retrying");
                continue;
            }

            /* ── 3a. BATCH_START → Class-C multi-frame flash transfer ───── */
            if (hdr.packet_type == PACKET_BATCH_START) {
                ESP_LOGI(TAG, "BATCH_START received — Class-C multi-frame transfer");

                if (hdr.payload_length < (uint16_t)sizeof(batch_start_payload_t)) {
                    ESP_LOGE(TAG, "BATCH_START payload too small (%u bytes)", hdr.payload_length);
                    udp_protocol_send_ack(sock, &dest_addr, sendbuf, session_id, 0);
                    continue;
                }

                batch_start_payload_t bsp;
                memcpy(&bsp, recvbuf + sizeof(packet_header_t), sizeof(bsp));
                ESP_LOGI(TAG, "BATCH: %u frames @ %u fps", bsp.frame_count, bsp.fps);

                /* Restore TUI so progress indicators are visible during transfer. */
                lcd_log_set_player_active(false);
                /* Stop the player before writing to flash. */
                s_player_stop_requested = true;
                vTaskDelay(pdMS_TO_TICKS(200));

                esp_err_t ferr = flash_frames_write_begin(bsp.frame_count, bsp.fps, bsp.frame_sizes);
                if (ferr != ESP_OK) {
                    ESP_LOGE(TAG, "flash_frames_write_begin: %s", esp_err_to_name(ferr));
                    udp_protocol_send_ack(sock, &dest_addr, sendbuf, session_id, 0);
                    /* write_begin failed — player was stopped but won't be restarted. */
                    after_batch_commit      = false;
                    s_player_stop_requested = false;
                    /* This is a persistent condition (typically ESP_ERR_NO_MEM: the
                     * batch exceeds the flash partition) — an immediate retry cannot
                     * succeed.  Without a pause this branch re-HELLOs instantly and
                     * the server re-sends BATCH_START: a hot loop of ~5 requests per
                     * second observed in the field (30-frame batch vs 2.4 MB
                     * partition).  Surface the reason on the TUI and back off
                     * BATCH_RETRY_DELAY_S, the same pacing used after a batch that
                     * exited without COMMIT. */
                    lcd_log_show_wait("BATCH TOO BIG",
                                      "Frames exceed flash size",
                                      "Reduce template frames");
                    for (int w = 0; w < BATCH_RETRY_DELAY_S * 5; w++) {
                        vTaskDelay(pdMS_TO_TICKS(200));
                        lcd_log_spin();
                    }
                    continue;
                }

                udp_protocol_send_ack(sock, &dest_addr, sendbuf, session_id, 0);

                uint32_t batch_seq      = 0;
                bool     batch_done      = false;
                bool     batch_committed = false;
                /* Accumulate CRC of all received DATA payloads so the device can report
                 * it in subsequent HELLOs, enabling the server NOT_MODIFIED short-circuit. */
                uint32_t batch_data_crc = 0;

                while (!batch_done) {
                    rlen = recvfrom(sock, recvbuf, sizeof(recvbuf), 0,
                                    (struct sockaddr *)&src_addr, &src_len);
                    if (rlen <= 0) { ESP_LOGW(TAG, "BATCH: timeout"); batch_done = true; continue; }
                    if (read_header(recvbuf, rlen, &hdr) != 0) { batch_done = true; continue; }

                    if (hdr.packet_type == PACKET_BATCH_COMMIT) {
                        ferr = flash_frames_write_commit();
                        if (ferr == ESP_OK) {
                            lcd_log_info("BATCH committed — starting playback");
                            /* Store combined batch CRC so the next HELLO can skip re-download. */
                            current_frame_crc  = batch_data_crc;
                            after_batch_commit = true;   /* use long idle so player runs undisturbed */
                            /* Free recv buffer so the player can claim the DRAM on
                             * no-PSRAM devices.  Batch DATA bypasses frame_buffer
                             * entirely, so this buffer is not needed during playback. */
                            frame_buffer_free_recv();
                            s_player_stop_requested = false;
                            xSemaphoreGive(s_player_start_sem);
                            batch_committed = true;
                            /* Persist CRC so the next boot restores it into current_frame_crc
                             * and the first HELLO can receive NOT_MODIFIED instead of triggering
                             * a full BATCH re-download when the flash frames are still current. */
                            nvs_storage_write_batch_crc(batch_data_crc);
                            /* Reset stall counter — BATCH is committed, animation resumes. */
                            not_modified_no_batch_count = 0;
                        } else {
                            ESP_LOGE(TAG, "flash_frames_write_commit: %s", esp_err_to_name(ferr));
                            lcd_log_error("BATCH commit failed");
                        }
                        udp_protocol_send_ack(sock, &dest_addr, sendbuf, session_id, 0);
                        batch_done = true;
                        continue;
                    }

                    if (hdr.packet_type != PACKET_DATA) { batch_done = true; continue; }
                    if (hdr.session_id  != session_id)  { continue; }

                    if (hdr.seq < batch_seq) {
                        udp_protocol_send_ack(sock, &dest_addr, sendbuf, session_id, hdr.seq);
                        vTaskDelay(pdMS_TO_TICKS(20));
                        continue;
                    }
                    if (hdr.seq > batch_seq) { batch_done = true; continue; }

                    ferr = flash_frames_write_chunk(
                        recvbuf + sizeof(packet_header_t), hdr.payload_length);
                    if (ferr == ESP_OK) {
                        /* Accumulate CRC of successfully written payload bytes. */
                        batch_data_crc = esp_rom_crc32_le(
                            batch_data_crc,
                            recvbuf + sizeof(packet_header_t),
                            hdr.payload_length);
                    } else {
                        ESP_LOGE(TAG, "BATCH write chunk: %s", esp_err_to_name(ferr));
                    }
                    batch_seq++;
                    udp_protocol_send_ack(sock, &dest_addr, sendbuf, session_id, hdr.seq);
                    vTaskDelay(pdMS_TO_TICKS(20));
                }

                if (!batch_committed) {
                    /* Batch loop exited without a COMMIT (timeout, bad packet, or
                     * sequence gap).  flash_frames_write_begin already erased the
                     * partition, so the player cannot animate from flash anymore.
                     * Clear after_batch_commit so the idle shrinks to IDLE_DURATION_S
                     * (~1 s) and a fresh HELLO fires immediately, triggering a fast
                     * re-download from the server.  Without this, the device stays
                     * frozen for BATCH_IDLE_S (5 min) between every failed attempt. */
                    ESP_LOGW(TAG, "BATCH: loop exited without COMMIT — will retry after backoff");
                    after_batch_commit  = false;
                    /* Safe to clear — player is blocked on xSemaphoreTake; it will
                     * reset this flag itself when the next successful commit fires the
                     * semaphore. */
                    s_player_stop_requested = false;
                    /* flash_frames_write_begin() erased the flash partition, so the
                     * stored frame is gone.  Reset the CRC to 0 so the next HELLO
                     * reports "no frame" rather than the old CRC.  Without this the
                     * server matches the stale CRC in _deviceBatchCrc and replies
                     * NOT_MODIFIED — the player never gets xSemaphoreGive and stays
                     * blocked on s_player_start_sem indefinitely. */
                    current_frame_crc = 0;
                    /* Flash was erased — the NVS-persisted CRC is now stale.
                     * Write 0 so the next boot also reports "no frame". */
                    nvs_storage_write_batch_crc(0);
                    /* Signal idle section to use BATCH_RETRY_DELAY_S instead of
                     * IDLE_DURATION_S so a failed BATCH does not cause a tight
                     * 1-second retry loop that hammers the server.  The early-AUTH
                     * push from the server will break this idle immediately when a
                     * fresh batch is ready, so the delay is a ceiling, not a floor. */
                    batch_failed = true;
                }

                goto idle; /* skip regular DATA transfer */
            }

            /* ── 3b. Regular single-frame DATA transfer ──────────────────── */
            if (hdr.packet_type != PACKET_DATA) {
                ESP_LOGW(TAG, "Expected DATA after AUTH, got 0x%X — retrying", hdr.packet_type);
                continue;
            }

            frame_buffer_session_start(total_size);

            /* Process the first DATA packet we already peeked at. */
            frame_buffer_save_chunk(recvbuf + sizeof(packet_header_t), hdr.payload_length);
            udp_protocol_send_ack(sock, &dest_addr, sendbuf, session_id, hdr.seq);
            vTaskDelay(pdMS_TO_TICKS(20));

            uint32_t expected_seq      = 1; /* 0 already processed */
            int      ack_timeout_count = 0;
            bool     transfer_done     = false;

            while (!transfer_done) {
                rlen = recvfrom(sock, recvbuf, sizeof(recvbuf), 0,
                                (struct sockaddr *)&src_addr, &src_len);
                if (rlen <= 0) {
                    /* Timeout: our last ACK may have been lost, or the next DATA
                     * packet was dropped.  Resend the last ACK to prompt the server
                     * to retransmit.  Abort after ACK_RETRY_MAX consecutive timeouts. */
                    if (expected_seq > 0 && ack_timeout_count < ACK_RETRY_MAX) {
                        ack_timeout_count++;
                        ESP_LOGW(TAG, "DATA timeout at seq=%u — resending ACK (retry %d/%d)",
                                 expected_seq - 1, ack_timeout_count, ACK_RETRY_MAX);
                        udp_protocol_send_ack(sock, &dest_addr, sendbuf,
                                              session_id, expected_seq - 1);
                    } else {
                        ESP_LOGW(TAG, "DATA transfer stalled (seq=%u, retries=%d) — aborting session",
                                 expected_seq, ack_timeout_count);
                        transfer_done = true;
                    }
                    continue;
                }
                ack_timeout_count = 0; /* reset on any successful receive */

                if (read_header(recvbuf, rlen, &hdr) != 0) {
                    ESP_LOGW(TAG, "Bad packet during transfer");
                    transfer_done = true;
                    continue;
                }

                if (hdr.packet_type != PACKET_DATA) {
                    ESP_LOGW(TAG, "Expected DATA, got 0x%X — aborting", hdr.packet_type);
                    transfer_done = true;
                    continue;
                }

                /* Reject DATA from a stale session. */
                if (hdr.session_id != session_id) {
                    ESP_LOGW(TAG, "DATA session_id mismatch: got %u expected %u — discarding",
                             hdr.session_id, session_id);
                    continue;
                }

                const uint8_t *payload     = recvbuf + sizeof(packet_header_t);
                size_t         payload_len = hdr.payload_length;

                if (hdr.seq < expected_seq) {
                    /* Retransmitted chunk we already stored — ACK it, do not re-save. */
                    ESP_LOGD(TAG, "Retransmit seq=%u (expected=%u) — ACK only, no save",
                             hdr.seq, expected_seq);
                    udp_protocol_send_ack(sock, &dest_addr, sendbuf, session_id, hdr.seq);
                    vTaskDelay(pdMS_TO_TICKS(20));
                    continue;
                }

                if (hdr.seq > expected_seq) {
                    /* Sliding-window Go-Back-N: server sent ahead of expected seq.
                     * Discard and NAK by re-ACKing the last in-order seq so the server
                     * knows to retransmit from expected_seq. */
                    ESP_LOGD(TAG, "Ahead-of-order seq=%u (expected=%u) — NAK, re-ACK %u",
                             hdr.seq, expected_seq,
                             expected_seq > 0 ? expected_seq - 1 : 0);
                    if (expected_seq > 0) {
                        udp_protocol_send_ack(sock, &dest_addr, sendbuf,
                                              session_id, expected_seq - 1);
                    }
                    continue;
                }

                /* seq == expected_seq: new chunk. */
                frame_buffer_save_chunk(payload, payload_len);
                expected_seq++;

                udp_protocol_send_ack(sock, &dest_addr, sendbuf, session_id, hdr.seq);

                /* No on-screen drawing during transfer — SPI blocks the
                 * receive loop and causes DONE packet loss. */

                if (hdr.flags & FLAG_LAST) {
                    /* ── 4. DONE + render ────────────────────────────── */
                    uint32_t last_data_seq = hdr.seq;
                    ESP_LOGI(TAG, "Last DATA chunk (seq=%u) — waiting for DONE", last_data_seq);

                    bool got_done = false;
                    for (int done_try = 0; done_try < ACK_RETRY_MAX && !got_done; done_try++) {
                        rlen = recvfrom(sock, recvbuf, sizeof(recvbuf), 0,
                                        (struct sockaddr *)&src_addr, &src_len);
                        if (rlen <= 0) {
                            /* Server probably didn't receive our last ACK — resend. */
                            ESP_LOGW(TAG, "DONE timeout (try %d/%d) — resending ACK seq=%u",
                                     done_try + 1, ACK_RETRY_MAX, last_data_seq);
                            udp_protocol_send_ack(sock, &dest_addr, sendbuf,
                                                  session_id, last_data_seq);
                            continue;
                        }
                        packet_header_t done_hdr;
                        int hdr_ok = read_header(recvbuf, rlen, &done_hdr);
                        if (hdr_ok == 0 && done_hdr.packet_type == PACKET_DONE) {
                            got_done = true;
                        } else if (hdr_ok == 0 && done_hdr.packet_type == PACKET_DATA) {
                            /* Server retransmitted last DATA — ACK it, keep waiting. */
                            udp_protocol_send_ack(sock, &dest_addr, sendbuf,
                                                  session_id, done_hdr.seq);
                            done_try--;  /* don't count as a failed attempt */
                        } else {
                            ESP_LOGW(TAG, "DONE: unexpected pkt=0x%X (hdr_ok=%d, try=%d/%d)",
                                     hdr_ok == 0 ? done_hdr.packet_type : -1,
                                     hdr_ok, done_try + 1, ACK_RETRY_MAX);
                        }
                    }

                    if (got_done) {
                        /* Single-frame transfer complete.  For Class-C devices that
                         * already have a valid batch in flash, keep the long idle so
                         * the animation continues uninterrupted between HELLO cycles.
                         * For Class-A/B (no batch in flash) the flag stays false and
                         * the normal short idle resumes. */
                        after_batch_commit = flash_frames_valid();

                        ESP_LOGI(TAG, "DONE: decoding %u B (psram=%d flash=%d overflow=%d)",
                                 frame_buffer_compressed_size(),
                                 (int)frame_buffer_use_psram(),
                                 (int)frame_buffer_use_flash(),
                                 (int)frame_buffer_overflow());

                        bool rendered = false;

                        if (frame_buffer_overflow()) {
                            ESP_LOGE(TAG, "DONE: recv overflow this session — skipping render");
                            lcd_log_error("Frame too large for recv buffer — skipped");

                        } else if (frame_buffer_use_flash()) {
                            uint32_t flash_crc = 0;
                            ESP_LOGI(TAG, "DONE: rendering %u B from flash",
                                     (unsigned)frame_buffer_compressed_size());
                            if (lcd_render_from_flash(frame_buffer_partition(),
                                                       frame_buffer_compressed_size(),
                                                       &flash_crc) == 0) {
                                current_frame_crc = flash_crc;
                                rendered = true;
                            } else {
                                ESP_LOGE(TAG, "lcd_render_from_flash failed — frame skipped");
                            }

                        } else {
                            const uint8_t *buf  = frame_buffer_recv_ptr();
                            uint32_t       size = frame_buffer_compressed_size();

                            if (!buf || size == 0) {
                                ESP_LOGE(TAG, "Render: recv buffer empty — skipping");

                            } else if (buf[0] == FRAME_FLAG_DELTA) {
                                uint32_t delta_crc = 0;
                                if (lcd_render_delta(buf, size, &delta_crc) == 0) {
                                    current_frame_crc = delta_crc;
                                    rendered = true;
                                }

                            } else if (buf[0] == FRAME_FLAG_LZ4_PALETTE) {
                                if (lcd_render_lz4(buf, size) == 0) {
                                    current_frame_crc = esp_rom_crc32_le(0, buf, size);
                                    rendered = true;
                                }

                            } else if (frame_buffer_use_psram()) {
                                /* Legacy PSRAM path: full decode into frame buffer. */
                                int decoded = frame_decode(buf, size,
                                                           frame_buffer_frame_ptr(),
                                                           IMAGE_SIZE);
                                if (decoded < 0) {
                                    ESP_LOGE(TAG, "frame_decode failed — skipping render");
                                } else {
                                    lcd_render_full(frame_buffer_frame_ptr());
                                    current_frame_crc = esp_rom_crc32_le(0, buf, size);
                                    rendered = true;
                                }

                            } else if (buf[0] == FRAME_FLAG_PALETTE) {
                                if (lcd_render_palette(buf, size) == 0) {
                                    current_frame_crc = esp_rom_crc32_le(0, buf, size);
                                    rendered = true;
                                }

                            } else {
                                ESP_LOGE(TAG, "Render: unsupported frame flag 0x%02X — skipping",
                                         buf[0]);
                            }
                        }

                        if (rendered) {
                            /* Reset stall counter — a new frame was successfully rendered,
                             * so the NOT_MODIFIED countdown starts fresh. */
                            not_modified_no_batch_count = 0;
                            last_success_for_WDT = esp_timer_get_time();
                            /* Frame render already painted the full LCD —
                             * just switch to overlay mode, do NOT redraw TUI. */
                            lcd_log_set_has_frame(true);
                        } else {
                            /* Render failed — hide progress popup and show error. */
                            lcd_log_hide_progress();
                        }

                        /* Performance counter — log every TIME_CYCLE_COUNT frames. */
                        frame_count++;
                        if (frame_count >= TIME_CYCLE_COUNT) {
                            int64_t elapsed_us = esp_timer_get_time() - perf_start;
                            int sec  = (int)(elapsed_us / 1000000);
                            int msec = (int)((elapsed_us / 1000) % 1000);
                            ESP_LOGI(TAG, "%d frames in %d.%03d s  |  heap=%d  stack_hwm=%d",
                                     TIME_CYCLE_COUNT, sec, msec,
                                     (int)esp_get_free_heap_size(),
                                     (int)uxTaskGetStackHighWaterMark(NULL));
                            frame_count = 0;
                            perf_start  = esp_timer_get_time();
                        }
                    } else {
                        ESP_LOGW(TAG, "No DONE received after %d retries — aborting session",
                                 ACK_RETRY_MAX);
                    }

                    transfer_done = true;
                }
            }

            /* Clean up progress popup after transfer ends (success or failure).
             * If rendered: s_prog_shown was already reset → hide is a no-op.
             * If failed:   popup is visible → hide restores the TUI cleanly. */
            lcd_log_hide_progress();
        } /* end AUTH/transfer block */

        idle:
        /* ── 5. Idle period: send PING heartbeats, handle CONFIG ───────── */
        /* After BATCH_COMMIT the player runs from flash and does not need the
         * server — use a long idle so it can complete many animation cycles.
         * For single-frame mode keep the short 1-second cycle so new frames
         * surface quickly (NOT_MODIFIED makes the round-trip cheap).
         * After a failed BATCH use a medium backoff so the server is not hammered
         * with back-to-back BATCH download attempts; the early-AUTH push mechanism
         * still interrupts this idle the moment the server has a fresh batch. */
        const int64_t idle_duration_us = after_batch_commit
            ? (int64_t)BATCH_IDLE_S         * 1000000
            : batch_failed
                ? (int64_t)BATCH_RETRY_DELAY_S * 1000000
                : (int64_t)IDLE_DURATION_S     * 1000000;
        batch_failed = false; /* consumed — reset for next cycle */
        int64_t       idle_start       = esp_timer_get_time();
        int64_t       idle_end         = idle_start + idle_duration_us;
        /* On first boot set last_ping_time far enough in the past so the PING
         * fires on the very first idle check instead of after PING_INTERVAL_S. */
        int64_t       last_ping_time   = s_first_ping_done
            ? idle_start
            : idle_start - (int64_t)udp_protocol_get_ping_interval() * 1000000LL;

        uint32_t uptime_at_idle_start = (uint32_t)(idle_start / 1000000);

        while (true) {
            int64_t now       = esp_timer_get_time();
            int64_t remaining = idle_end - now;
            if (remaining <= 0) {
                break;
            }

            if ((now - last_ping_time) >=
                (int64_t)udp_protocol_get_ping_interval() * 1000000LL) {
                uint32_t uptime_s = uptime_at_idle_start +
                                    (uint32_t)((now - idle_start) / 1000000);
                udp_protocol_send_ping(sock, &dest_addr, sendbuf, uptime_s);
                last_ping_time    = now;
                s_first_ping_done = true;
            }

            /* Set recvfrom timeout to remaining idle time, capped at 1 s so PING
             * can fire on schedule without blocking longer than needed. */
            int64_t tv_us = (remaining < 1000000LL) ? remaining : 1000000LL;
            if (tv_us < 1000LL) tv_us = 1000LL; /* floor at 1 ms to avoid busy-wait */
            struct timeval tv_idle = {
                .tv_sec  = (long)(tv_us / 1000000LL),
                .tv_usec = (long)(tv_us % 1000000LL),
            };
            setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, &tv_idle, sizeof(tv_idle));

            rlen = recvfrom(sock, recvbuf, sizeof(recvbuf), 0,
                            (struct sockaddr *)&src_addr, &src_len);
            if (rlen <= 0) {
                continue; /* normal timeout — keep ticking */
            }

            if (read_header(recvbuf, rlen, &hdr) != 0) {
                continue; /* bad packet — ignore */
            }

            if (hdr.packet_type == PACKET_ACK) {
                ESP_LOGD(TAG, "PING ACK received");
            } else if (hdr.packet_type == PACKET_CONFIG) {
                const uint8_t *cfg_payload = (hdr.payload_length > 0)
                    ? recvbuf + sizeof(packet_header_t)
                    : NULL;
                udp_protocol_handle_config(&hdr, cfg_payload);
                /* handle_config calls esp_restart() if FLAG_RESTART is set;
                   code below is reached only when no restart was needed. */
            } else if (hdr.packet_type == PACKET_AUTH) {
                /* Server pushed a new frame — break idle and fetch immediately. */
                ESP_LOGI(TAG, "Early AUTH during idle — starting new transfer");
                break;
            } else {
                ESP_LOGD(TAG, "Unexpected packet type 0x%X during idle — ignored",
                         hdr.packet_type);
            }
        }
    }

    /* Unreachable in normal operation. */
    ESP_LOGE(TAG, "udp_client_task exiting — closing socket");
    close(sock);
    vTaskDelete(NULL);
}
