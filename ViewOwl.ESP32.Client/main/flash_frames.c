#include "flash_frames.h"
#include "config.h"

#include <string.h>
#include "esp_log.h"
#include "esp_partition.h"

static const char *TAG = "flash_frames";

/* ── Module state ──────────────────────────────────────────────────────── */

/* Partition handle — resolved once and cached. */
static const esp_partition_t *s_part = NULL;

/* Header cached in RAM during and after a batch write. */
static flash_frames_header_t s_hdr;

/* Whether the cached header reflects a valid committed partition. */
static bool s_valid = false;

/* Write cursor: next byte offset in the partition (relative to partition start). */
static uint32_t s_write_offset = 0;

/* 4-byte alignment buffer for partial flash writes.
 * NOR flash minimum write unit is 4 bytes; any sub-4-byte tail is
 * held here until the next chunk arrives or commit() pads it to 4. */
static uint8_t  s_align_buf[4];
static uint8_t  s_align_fill = 0;   /* bytes currently in s_align_buf */

/* ── Partition lookup ──────────────────────────────────────────────────── */

static esp_err_t get_partition(void)
{
    if (s_part) return ESP_OK;

    s_part = esp_partition_find_first(ESP_PARTITION_TYPE_DATA,
                                      ESP_PARTITION_SUBTYPE_ANY,
                                      FRAMES_PARTITION_LABEL);
    if (!s_part) {
        ESP_LOGE(TAG, "Partition \"%s\" not found — check partitions_custom.csv",
                 FRAMES_PARTITION_LABEL);
        return ESP_ERR_NOT_FOUND;
    }

    ESP_LOGI(TAG, "Partition found: label=%s offset=0x%08X size=0x%08X",
             s_part->label, (unsigned)s_part->address, (unsigned)s_part->size);
    return ESP_OK;
}

/* ── Write helpers ─────────────────────────────────────────────────────── */

/**
 * @brief Flushes s_align_buf to flash at s_write_offset, padding to 4 bytes with 0xFF.
 */
static esp_err_t flush_align_buf(void)
{
    if (s_align_fill == 0) return ESP_OK;

    /* Pad the tail to a 4-byte boundary with 0xFF (erased flash value). */
    while (s_align_fill < 4) {
        s_align_buf[s_align_fill++] = 0xFF;
    }

    esp_err_t err = esp_partition_write(s_part, s_write_offset, s_align_buf, 4);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "flush_align_buf: esp_partition_write failed at 0x%X: %s",
                 (unsigned)s_write_offset, esp_err_to_name(err));
        return err;
    }

    s_write_offset += 4;
    s_align_fill    = 0;
    return ESP_OK;
}

/* ── Public write API ──────────────────────────────────────────────────── */

esp_err_t flash_frames_write_begin(uint8_t frame_count, uint8_t fps,
                                   const uint32_t *frame_sizes)
{
    if (frame_count == 0 || frame_count > BATCH_MAX_FRAMES || !frame_sizes) {
        ESP_LOGE(TAG, "write_begin: invalid args (frame_count=%u)", frame_count);
        return ESP_ERR_INVALID_ARG;
    }

    esp_err_t err = get_partition();
    if (err != ESP_OK) return err;

    /* Build the header in RAM (written to flash only at commit). */
    memset(&s_hdr, 0, sizeof(s_hdr));
    s_hdr.frame_count = frame_count;
    s_hdr.fps         = fps;
    for (int i = 0; i < frame_count; i++) {
        s_hdr.frame_sizes[i] = frame_sizes[i];
    }

    /* Compute total bytes needed: header sector + all frame data. */
    uint32_t total_data = 0;
    for (int i = 0; i < frame_count; i++) total_data += frame_sizes[i];
    uint32_t total_needed = FLASH_FRAMES_HDR_SECTOR + total_data;

    /* Round up to 4 KB sector boundary for esp_partition_erase_range. */
    uint32_t erase_size = (total_needed + 0x0FFFu) & ~0x0FFFu;
    if (erase_size > s_part->size) {
        ESP_LOGE(TAG, "write_begin: %u bytes required but partition is only %u bytes",
                 (unsigned)erase_size, (unsigned)s_part->size);
        return ESP_ERR_NO_MEM;
    }

    ESP_LOGI(TAG, "Erasing %u bytes for %u frames (%u data bytes)",
             (unsigned)erase_size, frame_count, (unsigned)total_data);

    err = esp_partition_erase_range(s_part, 0, erase_size);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "erase failed: %s", esp_err_to_name(err));
        return err;
    }

    /* Frame data starts right after the header sector. */
    s_write_offset = FLASH_FRAMES_HDR_SECTOR;
    s_align_fill   = 0;
    s_valid        = false;

    ESP_LOGI(TAG, "write_begin: ready — %u frames, %u fps", frame_count, fps);
    return ESP_OK;
}

esp_err_t flash_frames_write_chunk(const uint8_t *data, size_t len)
{
    if (!s_part) {
        ESP_LOGE(TAG, "write_chunk: not initialised — call write_begin first");
        return ESP_ERR_INVALID_STATE;
    }
    if (!data || len == 0) return ESP_OK;

    size_t pos = 0;

    /* Drain the alignment buffer first. */
    while (pos < len && s_align_fill > 0) {
        s_align_buf[s_align_fill++] = data[pos++];
        if (s_align_fill == 4) {
            esp_err_t e = esp_partition_write(s_part, s_write_offset, s_align_buf, 4);
            if (e != ESP_OK) return e;
            s_write_offset += 4;
            s_align_fill    = 0;
        }
    }

    /* Write aligned 4-byte chunks directly. */
    size_t aligned_len = (len - pos) & ~3u;
    if (aligned_len > 0) {
        esp_err_t e = esp_partition_write(s_part, s_write_offset,
                                          data + pos, aligned_len);
        if (e != ESP_OK) return e;
        s_write_offset += (uint32_t)aligned_len;
        pos            += aligned_len;
    }

    /* Buffer any remaining sub-4-byte tail. */
    while (pos < len) {
        s_align_buf[s_align_fill++] = data[pos++];
    }

    return ESP_OK;
}

esp_err_t flash_frames_write_commit(void)
{
    if (!s_part) {
        ESP_LOGE(TAG, "commit: not initialised");
        return ESP_ERR_INVALID_STATE;
    }

    /* Flush any remaining sub-4-byte data. */
    esp_err_t err = flush_align_buf();
    if (err != ESP_OK) return err;

    /* Write the header with the commit magic at offset 0.
     * The header sector was erased in write_begin(), so this is a fresh write. */
    s_hdr.magic = FLASH_FRAMES_MAGIC;

    /* Pad header to 4-byte multiple for esp_partition_write. */
    uint8_t hdr_buf[sizeof(flash_frames_header_t) + 3];
    memcpy(hdr_buf, &s_hdr, sizeof(s_hdr));
    size_t write_len = (sizeof(s_hdr) + 3u) & ~3u;

    err = esp_partition_write(s_part, 0, hdr_buf, write_len);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "commit: header write failed: %s", esp_err_to_name(err));
        return err;
    }

    s_valid = true;
    ESP_LOGI(TAG, "commit: %u frames committed to flash (total data %u bytes from offset 0x%X)",
             s_hdr.frame_count,
             (unsigned)(s_write_offset - FLASH_FRAMES_HDR_SECTOR),
             (unsigned)FLASH_FRAMES_HDR_SECTOR);
    return ESP_OK;
}

/* ── Public read API ───────────────────────────────────────────────────── */

bool flash_frames_valid(void)
{
    if (s_valid) return true;

    /* Not yet validated in this boot — read the header from flash. */
    esp_err_t err = get_partition();
    if (err != ESP_OK) return false;

    flash_frames_header_t hdr;
    err = esp_partition_read(s_part, 0, &hdr, sizeof(hdr));
    if (err != ESP_OK) return false;

    if (hdr.magic != FLASH_FRAMES_MAGIC
        || hdr.frame_count == 0
        || hdr.frame_count > BATCH_MAX_FRAMES) {
        return false;
    }

    /* Cache in RAM so subsequent calls are cheap. */
    memcpy(&s_hdr, &hdr, sizeof(hdr));
    s_valid = true;
    ESP_LOGI(TAG, "valid: %u frames @ %u fps restored from flash",
             s_hdr.frame_count, s_hdr.fps);
    return true;
}

uint8_t flash_frames_count(void)
{
    return s_valid ? s_hdr.frame_count : 0;
}

uint8_t flash_frames_fps(void)
{
    return s_valid ? s_hdr.fps : 0;
}

uint32_t flash_frames_max_frame_size(void)
{
    if (!s_valid) return 0u;

    uint32_t max_sz = 0u;
    for (uint8_t i = 0u; i < s_hdr.frame_count; i++) {
        if (s_hdr.frame_sizes[i] > max_sz) {
            max_sz = s_hdr.frame_sizes[i];
        }
    }
    return max_sz;
}

esp_err_t flash_frames_read(uint8_t idx, uint8_t *buf,
                            size_t buf_size, size_t *out_len)
{
    if (!buf || !out_len) return ESP_ERR_INVALID_ARG;
    *out_len = 0;

    if (!s_valid) {
        ESP_LOGE(TAG, "read: partition not valid");
        return ESP_ERR_INVALID_STATE;
    }

    if (idx >= s_hdr.frame_count) {
        ESP_LOGE(TAG, "read: idx=%u >= frame_count=%u", idx, s_hdr.frame_count);
        return ESP_ERR_INVALID_ARG;
    }

    uint32_t frame_size = s_hdr.frame_sizes[idx];
    if (frame_size > buf_size) {
        ESP_LOGE(TAG, "read: frame %u size %u > buf_size %u",
                 idx, (unsigned)frame_size, (unsigned)buf_size);
        return ESP_ERR_NO_MEM;
    }

    /* Compute offset: header sector + sum of all preceding frame sizes. */
    uint32_t offset = FLASH_FRAMES_HDR_SECTOR;
    for (uint8_t i = 0; i < idx; i++) offset += s_hdr.frame_sizes[i];

    esp_err_t err = esp_partition_read(s_part, offset, buf, frame_size);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "read: esp_partition_read failed: %s", esp_err_to_name(err));
        return err;
    }

    *out_len = frame_size;
    return ESP_OK;
}
