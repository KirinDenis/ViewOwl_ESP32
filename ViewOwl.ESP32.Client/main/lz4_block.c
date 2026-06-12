#include "lz4_block.h"
#include <string.h>
#include "esp_log.h"

static const char *TAG = "lz4_block";

int lz4_block_decompress(const uint8_t *src, int src_len,
                          uint8_t *dst,       int dst_cap)
{
    const uint8_t *sp      = src;
    const uint8_t *src_end = src + src_len;
    uint8_t       *dp      = dst;
    const uint8_t *dst_end = dst + dst_cap;

    while (sp < src_end) {
        uint8_t token = *sp++;

        /* ── Literal run ──────────────────────────────────────────────── */
        int lit_len = token >> 4;
        if (lit_len == 15) {
            uint8_t extra;
            do {
                if (sp >= src_end) {
                    ESP_LOGE(TAG, "truncated literal-length extra bytes");
                    return -1;
                }
                extra = *sp++;
                lit_len += extra;
            } while (extra == 255);
        }

        if (dp + lit_len > dst_end) {
            ESP_LOGE(TAG, "dst overflow in literal copy (lit_len=%d)", lit_len);
            return -1;
        }
        if (sp + lit_len > src_end) {
            ESP_LOGE(TAG, "src underrun in literal copy (lit_len=%d)", lit_len);
            return -1;
        }
        memcpy(dp, sp, (size_t)lit_len);
        sp += lit_len;
        dp += lit_len;

        /* Last sequence ends here — no match part follows. */
        if (sp >= src_end) break;

        /* ── Back-reference match ─────────────────────────────────────── */
        if (sp + 2 > src_end) {
            ESP_LOGE(TAG, "truncated match offset");
            return -1;
        }
        int offset = (int)sp[0] | ((int)sp[1] << 8);
        sp += 2;
        if (offset == 0) {
            ESP_LOGE(TAG, "invalid zero match offset");
            return -1;
        }

        /* Minimum match length is 4; low nibble 15 → read extra bytes. */
        int match_len = (token & 0x0F) + 4;
        if ((token & 0x0F) == 15) {
            uint8_t extra;
            do {
                if (sp >= src_end) {
                    ESP_LOGE(TAG, "truncated match-length extra bytes");
                    return -1;
                }
                extra = *sp++;
                match_len += extra;
            } while (extra == 255);
        }

        const uint8_t *mp = dp - offset;
        if (mp < dst) {
            ESP_LOGE(TAG, "match offset %d out of range (dp-dst=%d)", offset, (int)(dp - dst));
            return -1;
        }
        if (dp + match_len > dst_end) {
            ESP_LOGE(TAG, "dst overflow in match copy (match_len=%d)", match_len);
            return -1;
        }

        /* Byte-by-byte to handle correctly overlapping (run-length) copies
         * where offset < match_len (e.g. offset=1 repeats the last byte). */
        for (int i = 0; i < match_len; i++) {
            dp[i] = mp[i];
        }
        dp += match_len;
    }

    return (int)(dp - dst);
}
