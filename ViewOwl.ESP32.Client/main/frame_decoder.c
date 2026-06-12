#include "frame_decoder.h"
#include "lz4_block.h"
#include <string.h>
#include "esp_log.h"

static const char *TAG = "frame_decoder";

int frame_stream_init(frame_stream_t *s, const uint8_t *src, size_t src_len)
{
    if (!s || !src || src_len < 2) {
        return -1;
    }
    if (src[0] != FRAME_FLAG_PALETTE) {
        ESP_LOGE(TAG, "stream_init: expected FRAME_FLAG_PALETTE, got 0x%02X", src[0]);
        return -1;
    }

    uint8_t pal_len = src[1];
    if (pal_len == 0) {
        ESP_LOGE(TAG, "stream_init: palLen=0 is reserved");
        return -1;
    }

    size_t pal_bytes   = (size_t)pal_len * 2;
    size_t header_size = 2 + pal_bytes;
    if (src_len < header_size) {
        ESP_LOGE(TAG, "stream_init: src_len=%u < header_size=%u",
                 (unsigned)src_len, (unsigned)header_size);
        return -1;
    }

    s->palette    = src + 2;
    s->pal_len    = pal_len;
    s->rle        = src + header_size;
    s->rle_len    = src_len - header_size;
    s->rle_pos    = 0;
    s->run_remain = 0;
    s->run_hi     = 0;
    s->run_lo     = 0;
    return 0;
}

int frame_stream_read(frame_stream_t *s, uint8_t *dst, size_t n_pixels)
{
    if (!s || !dst) return -1;

    size_t written = 0;

    while (written < n_pixels) {
        /* Emit pixels remaining in the current run first. */
        if (s->run_remain > 0) {
            dst[written * 2]     = s->run_hi;
            dst[written * 2 + 1] = s->run_lo;
            written++;
            s->run_remain--;
            continue;
        }

        /* No active run — fetch the next RLE pair. */
        if (s->rle_pos + 2 > s->rle_len) {
            break; /* end of stream */
        }

        uint8_t count = s->rle[s->rle_pos];
        uint8_t idx   = s->rle[s->rle_pos + 1];
        s->rle_pos += 2;

        if (count == 0) {
            /* Reserved no-op — skip. */
            continue;
        }

        if (idx >= s->pal_len) {
            ESP_LOGE(TAG, "stream_read: index %u >= palLen %u at rle_pos=%u",
                     idx, s->pal_len, (unsigned)(s->rle_pos - 2));
            return -1;
        }

        s->run_hi     = s->palette[idx * 2];
        s->run_lo     = s->palette[idx * 2 + 1];
        s->run_remain = count;
    }

    if (written == 0) return 0; /* end of stream */
    return (int)written;
}

/* ── LZ4 streaming decoder ───────────────────────────────────────────── */

int frame_lz4_stream_init(frame_lz4_stream_t *s,
                           const uint8_t *src, size_t src_len,
                           uint32_t total_pixels, uint8_t *block_buf)
{
    if (!s || !src || !block_buf) return -1;

    if (src_len < 2 || src[0] != FRAME_FLAG_LZ4_PALETTE) {
        ESP_LOGE(TAG, "lz4_stream_init: expected 0x%02X, got 0x%02X",
                 FRAME_FLAG_LZ4_PALETTE, src ? src[0] : 0xFF);
        return -1;
    }

    uint8_t pal_len = src[1];
    if (pal_len == 0) {
        ESP_LOGE(TAG, "lz4_stream_init: palLen=0 is reserved");
        return -1;
    }

    /* Header: flag(1) + palLen(1) + palette(pal_len*2) + block_count(2) */
    size_t pal_bytes  = (size_t)pal_len * 2;
    size_t hdr_size   = 2 + pal_bytes + 2;
    if (src_len < hdr_size) {
        ESP_LOGE(TAG, "lz4_stream_init: src_len=%u < hdr_size=%u",
                 (unsigned)src_len, (unsigned)hdr_size);
        return -1;
    }

    size_t bc_off = 2 + pal_bytes;
    uint16_t block_count = (uint16_t)(src[bc_off] | ((uint16_t)src[bc_off + 1] << 8));

    s->palette       = src + 2;
    s->pal_len       = pal_len;
    s->blocks_start  = src + hdr_size;
    s->block_count   = block_count;
    s->block_idx     = 0;
    s->total_pixels  = total_pixels;
    s->pixel_pos     = 0;
    s->block_src_pos = 0;
    s->block_buf     = block_buf;
    s->block_buf_len = 0;
    s->block_buf_pos = 0;
    return 0;
}

/**
 * @brief Decompresses (or copies) the next block into s->block_buf.
 * @return 1 on success, 0 if no more blocks, -1 on error.
 */
static int lz4_load_next_block(frame_lz4_stream_t *s)
{
    if (s->block_idx >= s->block_count) return 0;

    const uint8_t *bp = s->blocks_start + s->block_src_pos;

    /* size_field: bit 15 = stored (raw copy), bits 14:0 = payload size */
    uint16_t size_field = (uint16_t)(bp[0] | ((uint16_t)bp[1] << 8));
    bool     stored     = (size_field & 0x8000) != 0;
    int      comp_size  = (int)(size_field & 0x7FFF);
    bp += 2;

    /* Expected decompressed size: LZ4_BLOCK_SIZE for all blocks except the last. */
    uint32_t pixels_done = (uint32_t)s->block_idx * LZ4_BLOCK_SIZE;
    uint32_t remaining   = s->total_pixels - pixels_done;
    int      exp_size    = (int)(remaining < LZ4_BLOCK_SIZE ? remaining : LZ4_BLOCK_SIZE);

    if (stored) {
        if (comp_size != exp_size) {
            ESP_LOGE(TAG, "lz4 block %u: stored size %d != expected %d",
                     s->block_idx, comp_size, exp_size);
            return -1;
        }
        memcpy(s->block_buf, bp, (size_t)comp_size);
        s->block_buf_len = comp_size;
    } else {
        int dec = lz4_block_decompress(bp, comp_size, s->block_buf, LZ4_BLOCK_SIZE);
        if (dec != exp_size) {
            ESP_LOGE(TAG, "lz4 block %u: decomp=%d expected=%d", s->block_idx, dec, exp_size);
            return -1;
        }
        s->block_buf_len = dec;
    }

    s->block_src_pos += 2 + (size_t)comp_size;
    s->block_buf_pos  = 0;
    s->block_idx++;
    return 1;
}

int frame_lz4_stream_read(frame_lz4_stream_t *s, uint8_t *dst, size_t n_pixels)
{
    if (!s || !dst) return -1;

    size_t written = 0;
    while (written < n_pixels) {
        /* Refill when the current block is exhausted. */
        if (s->block_buf_pos >= s->block_buf_len) {
            int r = lz4_load_next_block(s);
            if (r == 0) break;   /* end of stream */
            if (r < 0) return -1;
        }

        uint8_t idx = s->block_buf[s->block_buf_pos++];
        if (idx >= s->pal_len) {
            ESP_LOGE(TAG, "lz4_stream_read: index %u >= palLen %u", idx, s->pal_len);
            return -1;
        }

        dst[written * 2]     = s->palette[idx * 2];
        dst[written * 2 + 1] = s->palette[idx * 2 + 1];
        written++;
        s->pixel_pos++;
    }

    if (written == 0) return 0;
    return (int)written;
}

/* ── Full-frame decode ───────────────────────────────────────────────── */

int frame_decode(const uint8_t *src, size_t src_len, uint8_t *dst, size_t dst_size)
{
    if (!src || src_len < 1 || !dst || dst_size == 0) {
        ESP_LOGE(TAG, "decode: invalid arguments");
        return -1;
    }

    uint8_t flag = src[0];

    /* ── Raw mode: copy pixels directly ──────────────────────────────── */
    if (flag == FRAME_FLAG_RAW) {
        size_t pixel_bytes = src_len - 1;
        if (pixel_bytes > dst_size) {
            ESP_LOGE(TAG, "decode raw: src_len-1=%u > dst_size=%u",
                     (unsigned)pixel_bytes, (unsigned)dst_size);
            return -1;
        }
        memcpy(dst, src + 1, pixel_bytes);
        return (int)pixel_bytes;
    }

    /* ── Palette+RLE mode ─────────────────────────────────────────────── */
    if (flag != FRAME_FLAG_PALETTE) {
        ESP_LOGE(TAG, "decode: unknown flag 0x%02X", flag);
        return -1;
    }

    if (src_len < 2) {
        ESP_LOGE(TAG, "decode palette: too short for palLen byte");
        return -1;
    }

    uint8_t pal_len = src[1]; /* 1..255 (0 is theoretically 256 entries but we reserve it) */
    if (pal_len == 0) {
        ESP_LOGE(TAG, "decode palette: palLen=0 is reserved");
        return -1;
    }

    size_t pal_bytes   = (size_t)pal_len * 2;
    size_t header_size = 2 + pal_bytes;

    if (src_len < header_size) {
        ESP_LOGE(TAG, "decode palette: src_len=%u < header_size=%u",
                 (unsigned)src_len, (unsigned)header_size);
        return -1;
    }

    /* Build palette lookup: index → two raw bytes (big-endian RGB565). */
    const uint8_t *pal = src + 2;

    /* Decode RLE stream into dst. */
    const uint8_t *rle     = src + header_size;
    size_t         rle_len = src_len - header_size;
    size_t         dst_pos = 0;

    for (size_t i = 0; i + 1 < rle_len; i += 2) {
        uint8_t count = rle[i];
        uint8_t idx   = rle[i + 1];

        if (count == 0) {
            /* Reserved — future delta/skip opcode; treat as no-op for now. */
            continue;
        }

        if (idx >= pal_len) {
            ESP_LOGE(TAG, "decode rle: index %u >= palLen %u at rle[%u]",
                     idx, pal_len, (unsigned)i);
            return -1;
        }

        uint8_t hi = pal[idx * 2];
        uint8_t lo = pal[idx * 2 + 1];

        for (uint8_t r = 0; r < count; r++) {
            if (dst_pos + 2 > dst_size) {
                ESP_LOGE(TAG, "decode rle: dst overflow at dst_pos=%u", (unsigned)dst_pos);
                return -1;
            }
            dst[dst_pos++] = hi;
            dst[dst_pos++] = lo;
        }
    }

    return (int)dst_pos;
}
