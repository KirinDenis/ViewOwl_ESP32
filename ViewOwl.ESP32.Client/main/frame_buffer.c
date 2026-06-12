#include "frame_buffer.h"
#include "config.h"

#include <string.h>

#include "esp_heap_caps.h"
#include "esp_log.h"
#include "esp_err.h"

static const char *TAG = "frame_buffer";

/* Worst-case compressed frame: raw flag byte + all uncompressed pixels. */
#define RECV_BUF_SIZE (IMAGE_SIZE + 1u)

/* Fallback receive buffer sizes tried in order when PSRAM is unavailable.
 * Largest first: we take the biggest block the heap can satisfy.
 * On this board D/IRAM (~111 KiB) is the primary candidate after WiFi eats
 * ~10-15 KiB, leaving ~95-100 KiB free.  Clock/complex templates produce
 * ~103 KB compressed; probe in 1 KB steps to find the true heap limit. */
static const uint32_t k_dram_fallback_sizes[] = {
    106u * 1024u,
    105u * 1024u,
    104u * 1024u,
    103u * 1024u,
    100u * 1024u,
     90u * 1024u,
     80u * 1024u,
};
#define DRAM_FALLBACK_COUNT \
    (sizeof(k_dram_fallback_sizes) / sizeof(k_dram_fallback_sizes[0]))

/* ── Module state ─────────────────────────────────────────────────────── */

static uint8_t  *s_recv_buf     = NULL;
static uint32_t  s_recv_buf_cap = 0;
static uint8_t  *s_frame_buf    = NULL;
static bool      s_use_psram    = false;

static const esp_partition_t *s_img_partition     = NULL;
static uint32_t               s_flash_write_offset = 0;
static bool                   s_use_flash          = false;

static uint32_t s_compressed_size = 0;
static bool     s_recv_overflow   = false;

/* ── Helpers ──────────────────────────────────────────────────────────── */

/**
 * @brief Finds and logs the image flash partition.
 *
 * Separated out so it is called from both PSRAM and DRAM init paths.
 */
static void locate_image_partition(void)
{
    s_img_partition = esp_partition_find_first(
        ESP_PARTITION_TYPE_DATA, ESP_PARTITION_SUBTYPE_ANY, FRAMES_PARTITION_LABEL);

    if (s_img_partition) {
        ESP_LOGI(TAG, "Image partition '%s': size=%u B at 0x%08X",
                 FRAMES_PARTITION_LABEL,
                 (unsigned)s_img_partition->size,
                 (unsigned)s_img_partition->address);
    } else {
        ESP_LOGW(TAG, "Image partition '%s' not found — flash receive path unavailable",
                 FRAMES_PARTITION_LABEL);
    }
}

/* ── Public ───────────────────────────────────────────────────────────── */

void frame_buffer_init(void)
{
    /* Try PSRAM first. */
    s_recv_buf  = heap_caps_malloc(RECV_BUF_SIZE, MALLOC_CAP_SPIRAM);
    s_frame_buf = heap_caps_malloc(IMAGE_SIZE,    MALLOC_CAP_SPIRAM);

    if (s_recv_buf && s_frame_buf) {
        s_use_psram    = true;
        s_recv_buf_cap = RECV_BUF_SIZE;
        ESP_LOGI(TAG, "PSRAM path: recv=%u B  frame=%u B",
                 (unsigned)RECV_BUF_SIZE, (unsigned)IMAGE_SIZE);
        locate_image_partition();
        return;
    }

    /* PSRAM not available — release any partial allocation. */
    if (s_recv_buf)  { heap_caps_free(s_recv_buf);  s_recv_buf  = NULL; }
    if (s_frame_buf) { heap_caps_free(s_frame_buf); s_frame_buf = NULL; }

    /* Try DRAM fallback sizes from largest to smallest.
     * MALLOC_CAP_8BIT | MALLOC_CAP_INTERNAL allows the D/IRAM region alongside
     * pure DRAM; DMA capability is not required for the receive buffer. */
    for (size_t i = 0; i < DRAM_FALLBACK_COUNT; i++) {
        s_recv_buf = heap_caps_malloc(k_dram_fallback_sizes[i],
                                      MALLOC_CAP_8BIT | MALLOC_CAP_INTERNAL);
        if (s_recv_buf) {
            s_recv_buf_cap = k_dram_fallback_sizes[i];
            s_use_psram    = false;
            ESP_LOGW(TAG, "No PSRAM — DRAM fallback: recv cap=%u B (try %u/%u)",
                     (unsigned)s_recv_buf_cap,
                     (unsigned)(i + 1), (unsigned)DRAM_FALLBACK_COUNT);
            locate_image_partition();
            return;
        }
        ESP_LOGW(TAG, "Cannot allocate %u B (try %u/%u) — trying smaller",
                 (unsigned)k_dram_fallback_sizes[i],
                 (unsigned)(i + 1), (unsigned)DRAM_FALLBACK_COUNT);
    }

    /* All sizes exhausted: operate without a receive buffer.
     * save_chunk guards against NULL so the task stays alive.
     * Every incoming frame will be aborted with an overflow log. */
    s_recv_buf     = NULL;
    s_recv_buf_cap = 0;
    s_use_psram    = false;
    ESP_LOGE(TAG, "All fallback sizes failed — operating without receive buffer");
    locate_image_partition();
}

void frame_buffer_session_start(uint32_t total_size)
{
    s_compressed_size    = 0;
    s_recv_overflow      = false;
    s_use_flash          = false;
    s_flash_write_offset = 0;

    /* Activate flash path when the frame is too large for the DRAM/PSRAM buffer. */
    if (total_size > FLASH_THRESHOLD && s_img_partition != NULL) {
        uint32_t erase_size = (total_size + s_img_partition->erase_size - 1u)
                              / s_img_partition->erase_size
                              * s_img_partition->erase_size;
        if (erase_size > s_img_partition->size) {
            erase_size = s_img_partition->size;
        }
        ESP_LOGI(TAG, "Large frame (%u B > threshold %u B) — erasing %u B of flash",
                 (unsigned)total_size, (unsigned)FLASH_THRESHOLD, (unsigned)erase_size);
        esp_err_t err = esp_partition_erase_range(s_img_partition, 0, erase_size);
        if (err == ESP_OK) {
            s_use_flash = true;
            ESP_LOGI(TAG, "Flash partition ready — receiving into flash");
        } else {
            ESP_LOGE(TAG, "Partition erase failed: %s — falling back to DRAM buffer",
                     esp_err_to_name(err));
        }
    }
}

void frame_buffer_save_chunk(const uint8_t *data, size_t len)
{
    if (s_recv_overflow) {
        /* Overflow already flagged this session — discard silently. */
        return;
    }

    if (s_use_flash) {
        if (!s_img_partition) {
            ESP_LOGE(TAG, "Flash path active but partition pointer is NULL");
            s_recv_overflow = true;
            return;
        }
        if (s_flash_write_offset + (uint32_t)len > s_img_partition->size) {
            ESP_LOGE(TAG, "Flash overflow: offset=%u + len=%u > part_size=%u — aborting",
                     (unsigned)s_flash_write_offset, (unsigned)len,
                     (unsigned)s_img_partition->size);
            s_recv_overflow = true;
            return;
        }
        esp_err_t err = esp_partition_write(s_img_partition,
                                            s_flash_write_offset, data, len);
        if (err != ESP_OK) {
            ESP_LOGE(TAG, "esp_partition_write failed: %s — aborting",
                     esp_err_to_name(err));
            s_recv_overflow = true;
            return;
        }
        s_flash_write_offset += (uint32_t)len;
        s_compressed_size    += (uint32_t)len;
        return;
    }

    /* DRAM / PSRAM path. */
    if (!s_recv_buf) return;

    if (s_compressed_size + (uint32_t)len > s_recv_buf_cap) {
        ESP_LOGE(TAG, "Recv overflow: cur=%u + len=%u > cap=%u — aborting session",
                 (unsigned)s_compressed_size, (unsigned)len, (unsigned)s_recv_buf_cap);
        s_recv_overflow = true;
        return;
    }

    memcpy(s_recv_buf + s_compressed_size, data, len);
    s_compressed_size += (uint32_t)len;
}

bool                  frame_buffer_overflow(void)        { return s_recv_overflow; }
bool                  frame_buffer_use_flash(void)       { return s_use_flash; }
bool                  frame_buffer_use_psram(void)       { return s_use_psram; }
uint32_t              frame_buffer_compressed_size(void) { return s_compressed_size; }
const uint8_t        *frame_buffer_recv_ptr(void)        { return s_recv_buf; }
uint8_t              *frame_buffer_frame_ptr(void)       { return s_frame_buf; }
const esp_partition_t *frame_buffer_partition(void)      { return s_img_partition; }

void frame_buffer_free_recv(void)
{
    if (s_recv_buf && !s_use_psram) {
        /* Only free DRAM buffers — PSRAM is abundant and not worth releasing. */
        heap_caps_free(s_recv_buf);
        s_recv_buf     = NULL;
        s_recv_buf_cap = 0;
        ESP_LOGI(TAG, "recv buffer freed — heap available for frame player");
    }
}
