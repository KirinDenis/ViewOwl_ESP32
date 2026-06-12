#pragma once

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

#include "esp_partition.h"

/**
 * @brief Allocates receive and frame decode buffers.
 *
 * Must be called once at task startup before any other frame_buffer_* function.
 * Tries PSRAM first; falls back to internal DRAM in decreasing size steps.
 * Also locates the image flash partition for large-frame reception.
 */
void frame_buffer_init(void);

/**
 * @brief Resets per-session state and selects the storage path.
 *
 * Call after receiving AUTH with the total frame size from AUTH.seq.
 * When total_size exceeds FLASH_THRESHOLD and the image partition is available,
 * activates the flash receive path and erases the required sectors.
 *
 * @param total_size Total compressed frame size in bytes (from AUTH.seq).
 */
void frame_buffer_session_start(uint32_t total_size);

/**
 * @brief Appends one DATA payload chunk to the active storage path (buffer or flash).
 *
 * Sets the overflow flag when the data would exceed buffer or partition capacity;
 * subsequent calls are silently discarded until the next session_start.
 *
 * @param data Pointer to chunk bytes.
 * @param len  Chunk length in bytes.
 */
void frame_buffer_save_chunk(const uint8_t *data, size_t len);

/** @brief Returns true when a buffer or flash overflow occurred this session. */
bool frame_buffer_overflow(void);

/** @brief Returns true when this session is writing to the flash partition. */
bool frame_buffer_use_flash(void);

/** @brief Returns true when both recv and frame buffers are in PSRAM. */
bool frame_buffer_use_psram(void);

/** @brief Returns the number of compressed bytes accumulated this session. */
uint32_t frame_buffer_compressed_size(void);

/**
 * @brief Returns a read-only pointer to the compressed receive buffer.
 *
 * Valid only for non-flash sessions.  Returns NULL when the flash path is
 * active or when no buffer was allocated.
 */
const uint8_t *frame_buffer_recv_ptr(void);

/**
 * @brief Returns the PSRAM frame decode target buffer.
 *
 * Used by the legacy palette+RLE full-frame decode path.
 * Returns NULL when PSRAM is not available.
 */
uint8_t *frame_buffer_frame_ptr(void);

/**
 * @brief Returns the image flash partition handle.
 *
 * Returns NULL when the partition was not found during init.
 */
const esp_partition_t *frame_buffer_partition(void);

/**
 * @brief Releases the receive buffer back to the heap.
 *
 * Call after a successful BATCH_COMMIT so the frame player can claim the
 * freed memory for its playback buffer.  The receive buffer is not needed
 * during the BATCH_IDLE_S window because batch DATA arrives via the
 * flash_frames_write_* path, not through this buffer.
 *
 * If a single-frame transfer is subsequently attempted without PSRAM the
 * session will fall back to the flash receive path automatically.
 */
void frame_buffer_free_recv(void);
