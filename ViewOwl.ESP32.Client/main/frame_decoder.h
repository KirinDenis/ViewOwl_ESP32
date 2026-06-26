#pragma once
#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>

/**
 * @brief Frame compression flags (first byte of every .bin file).
 */
#define FRAME_FLAG_RAW         0x00  /**< Uncompressed: remainder is raw BGR565 pixels.            */
#define FRAME_FLAG_PALETTE     0x01  /**< Palette + RLE encoded frame.                             */
#define FRAME_FLAG_LZ4_PALETTE 0x03  /**< Palette + LZ4 independent-block compressed index stream. */
#define FRAME_FLAG_DELTA       0x04  /**< Incremental update: changed rectangular regions only.    */

/**
 * @brief Index bytes per LZ4 compressed block.
 * Must match LZ4BlockIndexSize in FrameCompressor.cs on the server.
 */
#define LZ4_BLOCK_SIZE 4096

/**
 * @brief Streaming palette+RLE decoder state.
 *
 * Allows decoding a FRAME_FLAG_PALETTE frame in strips without buffering the
 * entire decoded output — the caller fills one LCD strip at a time directly into
 * s_linebuf and sends it to the display.
 *
 * The source buffer @c src must remain valid for the lifetime of the stream.
 */
typedef struct {
    const uint8_t *palette;    /**< Pointer into src at the palette table.        */
    uint8_t        pal_len;    /**< Number of palette entries (1..255).           */
    const uint8_t *rle;        /**< Pointer into src at the RLE stream.           */
    size_t         rle_len;    /**< Length of the RLE stream in bytes.            */
    size_t         rle_pos;    /**< Current read position within the RLE stream.  */
    uint8_t        run_remain; /**< Pixels left in the current run.               */
    uint8_t        run_hi;     /**< High byte (MSB) of the current run's colour.  */
    uint8_t        run_lo;     /**< Low byte (LSB) of the current run's colour.   */
} frame_stream_t;

/**
 * @brief Initialises a streaming palette+RLE decoder.
 *
 * @param s       Uninitialised stream state to fill in.
 * @param src     Compressed frame buffer (must start with FRAME_FLAG_PALETTE).
 * @param src_len Length of @p src in bytes.
 *
 * @return 0 on success, -1 if @p src does not contain a valid palette frame.
 */
int frame_stream_init(frame_stream_t *s, const uint8_t *src, size_t src_len);

/**
 * @brief Decodes exactly @p n_pixels pixels into @p dst (2 × n_pixels bytes).
 *
 * @param s        Streaming decoder state (must be initialised with frame_stream_init).
 * @param dst      Output buffer (must hold at least 2 × n_pixels bytes).
 * @param n_pixels Number of pixels to decode.
 *
 * @return Number of pixels written on success, 0 at end-of-stream, -1 on error.
 */
int frame_stream_read(frame_stream_t *s, uint8_t *dst, size_t n_pixels);

/**
 * @brief Streaming LZ4-palette decoder state (FRAME_FLAG_LZ4_PALETTE).
 *
 * Decompresses one LZ4 block at a time into an external @c block_buf
 * (LZ4_BLOCK_SIZE bytes) so that no full-frame buffer is needed.
 * The compressed source buffer must remain valid for the stream lifetime.
 */
typedef struct {
    const uint8_t *palette;       /**< Pointer to palette table in source buffer.          */
    uint8_t        pal_len;       /**< Number of palette entries (1..255).                 */
    const uint8_t *blocks_start;  /**< Pointer to first block in source buffer.            */
    uint16_t       block_count;   /**< Total number of compressed blocks.                  */
    uint16_t       block_idx;     /**< Index of the next block to decompress.              */
    uint32_t       total_pixels;  /**< Total pixel count for this frame.                   */
    uint32_t       pixel_pos;     /**< Global pixel cursor (for bounds checking).          */
    size_t         block_src_pos; /**< Byte offset into blocks_start for next block.       */
    uint8_t       *block_buf;     /**< Caller-supplied decompression scratch (LZ4_BLOCK_SIZE B). */
    int            block_buf_len; /**< Valid decompressed bytes in block_buf.              */
    int            block_buf_pos; /**< Read cursor within block_buf.                       */
    uint16_t       pal16[256];    /**< Full 256-entry palette, each laid out as [hi,lo] bytes
                                       so the hot loop does one 16-bit store per pixel and
                                       needs no per-pixel range check.                     */
} frame_lz4_stream_t;

/**
 * @brief Initialises a streaming LZ4-palette decoder.
 *
 * @param s            Uninitialised stream state to fill in.
 * @param src          Compressed frame buffer (must start with FRAME_FLAG_LZ4_PALETTE).
 * @param src_len      Length of @p src in bytes.
 * @param total_pixels Expected pixel count (e.g. LCD_H_RES * LCD_V_RES).
 * @param block_buf    Caller-allocated scratch buffer of exactly LZ4_BLOCK_SIZE bytes.
 * @return 0 on success, -1 on error.
 */
int frame_lz4_stream_init(frame_lz4_stream_t *s,
                           const uint8_t *src, size_t src_len,
                           uint32_t total_pixels, uint8_t *block_buf);

/**
 * @brief Decodes exactly @p n_pixels pixels into @p dst (2 × n_pixels bytes, big-endian RGB565).
 *
 * @param s        Streaming decoder state (must be initialised).
 * @param dst      Output buffer (must hold at least 2 × n_pixels bytes).
 * @param n_pixels Number of pixels to decode.
 * @return Number of pixels written, 0 at end-of-stream, -1 on error.
 */
int frame_lz4_stream_read(frame_lz4_stream_t *s, uint8_t *dst, size_t n_pixels);

/**
 * @brief Decodes a compressed frame into a flat BGR565 pixel buffer.
 *
 * Supported frame formats:
 *
 * - FRAME_FLAG_RAW (0x00):
 *     src[0] = 0x00
 *     src[1..] = raw big-endian RGB565 pixels (width * height * 2 bytes)
 *
 * - FRAME_FLAG_PALETTE (0x01):
 *     src[0] = 0x01
 *     src[1] = palLen (number of palette entries, 1..255)
 *     src[2..2+palLen*2-1] = palette (big-endian RGB565, 2 bytes per entry)
 *     src[2+palLen*2..] = RLE pairs [count:uint8][index:uint8]
 *       count  — run length 1..255; value 0 is reserved (no-op, skip)
 *       index  — palette index
 *
 * @param src      Compressed frame data.
 * @param src_len  Length of compressed data in bytes.
 * @param dst      Output buffer for decoded BGR565 pixels.
 * @param dst_size Capacity of @p dst in bytes (must be >= width * height * 2).
 *
 * @return Number of bytes written to @p dst on success, or -1 on error.
 */
int frame_decode(const uint8_t *src, size_t src_len, uint8_t *dst, size_t dst_size);
