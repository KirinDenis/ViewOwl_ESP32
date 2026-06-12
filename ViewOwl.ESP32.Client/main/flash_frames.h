#pragma once

/**
 * @file flash_frames.h
 * @brief Flash-partition storage for Class-C multi-frame animation sequences.
 *
 * Layout inside the "frames" partition:
 *
 *   Offset 0x0000  — flash_frames_header_t  (4 KB sector reserved, ~76 bytes used)
 *   Offset 0x1000  — frame 0 compressed data  (frame_sizes[0] bytes)
 *   Offset 0x1000 + frame_sizes[0] — frame 1 compressed data
 *   ...
 *
 * The header magic field is written LAST (in flash_frames_commit) so that a
 * power-loss during transfer leaves the partition in an invalid state and the
 * device falls back to normal single-frame mode instead of playing back garbage.
 */

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>
#include "esp_err.h"
#include "packet.h"    /* BATCH_MAX_FRAMES */

/* ── Constants ─────────────────────────────────────────────────────────── */

/** Magic value stored in flash_frames_header_t.magic on a valid partition. */
#define FLASH_FRAMES_MAGIC    0xC1A55C1F

/** Size of the header sector (first 4 KB of the partition). */
#define FLASH_FRAMES_HDR_SECTOR 0x1000u

/* ── Stored header ─────────────────────────────────────────────────────── */

/**
 * @brief Persistent header written at the start of the "frames" partition.
 *
 * Written LAST (after all frame data) so the magic field acts as a commit flag.
 */
typedef struct {
    uint32_t magic;                          /* FLASH_FRAMES_MAGIC when valid           */
    uint8_t  frame_count;                    /* number of stored frames (1..16)         */
    uint8_t  fps;                            /* intended playback FPS                   */
    uint16_t reserved;                       /* must be zero                            */
    uint32_t frame_sizes[BATCH_MAX_FRAMES];  /* compressed byte length of each frame    */
} flash_frames_header_t;  /* 4 + 4 + 16*4 = 72 bytes */

/* ── Write API (called during BATCH transfer) ──────────────────────────── */

/**
 * @brief Prepares the flash partition for a new multi-frame batch write.
 *
 * Erases the required flash range (header sector + all frame data sectors) and
 * caches the batch metadata in RAM.  Frame data should be streamed in
 * immediately after via flash_frames_write_chunk().
 *
 * @param frame_count Number of frames (1..BATCH_MAX_FRAMES).
 * @param fps         Intended playback frame-rate.
 * @param frame_sizes Array of BATCH_MAX_FRAMES compressed frame sizes in bytes.
 * @return ESP_OK, or an esp_err_t error code on flash / parameter failure.
 */
esp_err_t flash_frames_write_begin(uint8_t frame_count, uint8_t fps,
                                   const uint32_t *frame_sizes);

/**
 * @brief Appends a chunk of compressed frame data to the partition.
 *
 * Chunks are written to the partition starting immediately after the header
 * sector (offset FLASH_FRAMES_HDR_SECTOR).  The function buffers writes
 * internally to satisfy the 4-byte flash alignment requirement.
 *
 * @param data Pointer to chunk bytes (must be in internal RAM, not PSRAM).
 * @param len  Chunk length in bytes.
 * @return ESP_OK, or an esp_err_t error code.
 */
esp_err_t flash_frames_write_chunk(const uint8_t *data, size_t len);

/**
 * @brief Finalises the batch transfer.
 *
 * Flushes any buffered write data, then writes the header (including the
 * FLASH_FRAMES_MAGIC commit flag) to offset 0 of the partition.  After this
 * call flash_frames_valid() returns true and playback can begin.
 *
 * @return ESP_OK, or an esp_err_t error code.
 */
esp_err_t flash_frames_write_commit(void);

/* ── Read / query API (called by frame player) ─────────────────────────── */

/**
 * @brief Returns true when a complete, committed frame set is stored.
 *
 * Reads the header magic from flash; returns false if the partition has never
 * been written or the last write was interrupted.
 */
bool flash_frames_valid(void);

/** @brief Number of stored frames (0 when invalid). */
uint8_t flash_frames_count(void);

/** @brief Stored playback FPS (0 when invalid). */
uint8_t flash_frames_fps(void);

/**
 * @brief Returns the largest compressed frame size stored in the partition.
 *
 * Used by the player task to allocate the minimum-sized read buffer instead
 * of over-allocating IMAGE_SIZE bytes (which fails on no-PSRAM devices).
 * Returns 0 when the partition is not valid.
 */
uint32_t flash_frames_max_frame_size(void);

/**
 * @brief Reads one compressed frame from flash into @p buf.
 *
 * @param idx      Frame index (0 .. flash_frames_count()-1).
 * @param buf      Destination buffer (must be in internal RAM for DMA safety).
 * @param buf_size Capacity of @p buf in bytes.
 * @param out_len  Receives the actual number of bytes read.
 * @return ESP_OK, ESP_ERR_INVALID_ARG when idx is out of range,
 *         or ESP_ERR_NO_MEM when @p buf_size < frame size.
 */
esp_err_t flash_frames_read(uint8_t idx, uint8_t *buf,
                            size_t buf_size, size_t *out_len);
