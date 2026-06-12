#pragma once
#include <stdint.h>

/**
 * @brief Decompresses a single raw LZ4 block (no frame wrapper).
 *
 * Designed for independent-block mode where every block is compressed
 * without referencing data outside its own output — no ring buffer or
 * external dictionary is required.
 *
 * Compatible with blocks produced by K4os.Compression.LZ4 LZ4Codec.Encode
 * on the server side.
 *
 * @param src      Compressed block data (standard LZ4 block format).
 * @param src_len  Compressed size in bytes.
 * @param dst      Output buffer for decompressed data.
 * @param dst_cap  Capacity of @p dst in bytes.
 * @return Number of decompressed bytes written, or -1 on error.
 */
int lz4_block_decompress(const uint8_t *src, int src_len,
                          uint8_t *dst,       int dst_cap);
