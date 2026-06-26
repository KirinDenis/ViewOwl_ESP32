#pragma once

#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>

#include "esp_partition.h"
#include "esp_lcd_panel_io.h"

#ifndef _ILI9486_LCD
/**
 * @brief on_color_trans_done callback — releases one ping-pong strip buffer.
 *
 * Registered on the panel IO in lcd_init().  Runs in ISR context: a strip's
 * RAMWR completion gives the buffer-free semaphore so the renderer may reuse
 * that buffer.  Not used on the ILI9486 raw-SPI (synchronous) path.
 */
bool lcd_strip_trans_done(esp_lcd_panel_io_handle_t panel_io,
                          esp_lcd_panel_io_event_data_t *edata,
                          void *user_ctx);
#endif

/**
 * @brief Sends a fully decoded BGR565 frame from PSRAM to the LCD strip-by-strip.
 *
 * Copies strips from @p frame_buf (PSRAM, not DMA-accessible) into the internal
 * SRAM line buffer before handing each strip to the SPI driver.
 *
 * @param frame_buf Pointer to IMAGE_SIZE bytes of decoded BGR565 pixels in PSRAM.
 */
void lcd_render_full(const uint8_t *frame_buf);

/**
 * @brief Decodes a FRAME_FLAG_PALETTE (palette+RLE) frame strip-by-strip to the LCD.
 *
 * No full-frame decode buffer required — each strip is decoded directly into the
 * internal SRAM line buffer and sent immediately.
 *
 * @param comp     Compressed frame data (flag byte 0x01 included).
 * @param comp_len Length of @p comp in bytes.
 * @return 0 on success, -1 on decode error.
 */
int lcd_render_palette(const uint8_t *comp, size_t comp_len);

/**
 * @brief Decodes a FRAME_FLAG_LZ4_PALETTE frame strip-by-strip to the LCD.
 *
 * Uses a single static 4 KB scratch buffer for per-block decompression.
 * Works regardless of PSRAM presence.
 *
 * @param comp     Compressed frame data (flag byte 0x03 included).
 * @param comp_len Length of @p comp in bytes.
 * @return 0 on success, -1 on decode error.
 */
int lcd_render_lz4(const uint8_t *comp, size_t comp_len);

/**
 * @brief Applies a FRAME_FLAG_DELTA patch to the LCD GRAM.
 *
 * Decompresses each changed region (LZ4) into the SRAM line buffer and writes
 * only those regions to the display via CASET/RASET/RAMWR window writes.
 *
 * @param data        Delta payload (flag byte 0x04 included).
 * @param len         Total length of the delta in bytes.
 * @param out_new_crc Receives the new_crc embedded in the delta header on success.
 * @return 0 on success, -1 on malformed header or decompress error.
 */
int lcd_render_delta(const uint8_t *data, size_t len, uint32_t *out_new_crc);

/**
 * @brief Memory-maps a flash partition and renders the stored compressed frame.
 *
 * The compressed data written during DATA reception is mapped into the CPU
 * address space so that the existing streaming decoders can consume it via a
 * plain pointer — no additional RAM buffer required.
 *
 * @param partition  Image flash partition (from frame_buffer_partition()).
 * @param comp_size  Bytes of compressed data in the partition this session.
 * @param out_crc    CRC32 of the mapped data on success (may be NULL).
 * @return 0 on success, -1 on mapping or decode failure.
 */
int lcd_render_from_flash(const esp_partition_t *partition,
                           uint32_t comp_size,
                           uint32_t *out_crc);
