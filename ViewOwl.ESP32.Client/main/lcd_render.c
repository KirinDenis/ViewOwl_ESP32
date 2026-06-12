#include "lcd_render.h"
#include "config.h"
#include "frame_decoder.h"
#include "lz4_block.h"
#include "lcd_init.h"

#include <string.h>

#include "esp_log.h"
#include "esp_rom_crc.h"
#include "esp_partition.h"
#include "spi_flash_mmap.h"

#include "esp_lcd_panel_io.h"
#include "esp_lcd_panel_vendor.h"
#include "esp_lcd_panel_ops.h"
#include "esp_lcd_panel_commands.h"

#ifdef _ILI9486_LCD
#include "esp_lcd_ili9486.h"
#endif

static const char *TAG = "lcd_render";

/* SRAM line-strip DMA buffer — must not be in PSRAM (DMA cannot read SPIRAM).
 * Sized for one strip of LCD_H_LINES rows across the full display width. */
static uint8_t s_linebuf[LCD_BUFFER_SIZE];

/* Static scratch for per-block LZ4 decompression — avoids 4 KB on the task stack. */
static uint8_t s_lz4_scratch[LZ4_BLOCK_SIZE];

/* ── Internal helpers ─────────────────────────────────────────────────── */

/**
 * @brief Writes one horizontal strip to the LCD via CASET/RASET/RAMWR.
 *
 * @param y      First row of the strip.
 * @param buf    Pointer to strip pixel data (LCD_BUFFER_SIZE bytes, SRAM).
 */
static void send_strip(int y, const uint8_t *buf)
{
#ifdef _ILI9486_LCD
    /* ILI9486: raw 16-bit SPI path — bypasses io_handle (8-bit, incompatible
     * with 74HC245 level-shifter buffers on Waveshare RPi LCD (A) V3). */
    ili9486_blit(0, y, LCD_H_RES, y + LCD_H_LINES, buf, LCD_BUFFER_SIZE);
#else
    ESP_ERROR_CHECK(esp_lcd_panel_io_tx_param(io_handle, LCD_CMD_CASET,
        (uint8_t[]){
            0,
            0,
            ((LCD_H_RES - 1) >> 8) & 0xFF,
            (LCD_H_RES - 1) & 0xFF,
        }, 4));

    ESP_ERROR_CHECK(esp_lcd_panel_io_tx_param(io_handle, LCD_CMD_RASET,
        (uint8_t[]){
            (y >> 8) & 0xFF,
            y & 0xFF,
            ((y + LCD_H_LINES - 1) >> 8) & 0xFF,
            (y + LCD_H_LINES - 1) & 0xFF,
        }, 4));

    ESP_ERROR_CHECK(esp_lcd_panel_io_tx_color(io_handle, LCD_CMD_RAMWR,
                                               buf, LCD_BUFFER_SIZE));
    /* Synchronisation NOP — drains the SPI queue before the next command. */
    ESP_ERROR_CHECK(esp_lcd_panel_io_tx_param(io_handle, 0x00, NULL, 0));
#endif
}

/* ── Public ───────────────────────────────────────────────────────────── */

void lcd_render_full(const uint8_t *frame_buf)
{
    for (int y = 0; y < LCD_V_RES; y += LCD_H_LINES) {
        /* Copy strip from PSRAM into SRAM before DMA hand-off. */
        uint32_t offset = (uint32_t)y * LCD_H_RES * sizeof(uint16_t);
        memcpy(s_linebuf, frame_buf + offset, LCD_BUFFER_SIZE);
        send_strip(y, s_linebuf);
    }
}

int lcd_render_palette(const uint8_t *comp, size_t comp_len)
{
    frame_stream_t stream;
    if (frame_stream_init(&stream, comp, comp_len) != 0) {
        ESP_LOGE(TAG, "lcd_render_palette: init failed (not a palette frame?)");
        return -1;
    }

    size_t pixels_per_strip = (size_t)LCD_H_RES * LCD_H_LINES;

    for (int y = 0; y < LCD_V_RES; y += LCD_H_LINES) {
        int got = frame_stream_read(&stream, s_linebuf, pixels_per_strip);
        if (got <= 0) {
            ESP_LOGE(TAG, "lcd_render_palette: stream ended early at y=%d", y);
            return -1;
        }
        send_strip(y, s_linebuf);
    }
    return 0;
}

int lcd_render_lz4(const uint8_t *comp, size_t comp_len)
{
    frame_lz4_stream_t stream;
    uint32_t total_pixels = (uint32_t)LCD_H_RES * (uint32_t)LCD_V_RES;

    if (frame_lz4_stream_init(&stream, comp, comp_len, total_pixels, s_lz4_scratch) != 0) {
        ESP_LOGE(TAG, "lcd_render_lz4: init failed");
        return -1;
    }

    size_t pixels_per_strip = (size_t)LCD_H_RES * LCD_H_LINES;

    for (int y = 0; y < LCD_V_RES; y += LCD_H_LINES) {
        int got = frame_lz4_stream_read(&stream, s_linebuf, pixels_per_strip);
        if (got <= 0) {
            ESP_LOGE(TAG, "lcd_render_lz4: stream ended early at y=%d", y);
            return -1;
        }
        send_strip(y, s_linebuf);
    }
    return 0;
}

int lcd_render_delta(const uint8_t *data, size_t len, uint32_t *out_new_crc)
{
    /* Minimum header: flag(1) + base_crc(4) + new_crc(4) + region_count(2) = 11 bytes */
    if (!data || len < 11u || data[0] != FRAME_FLAG_DELTA) {
        ESP_LOGE(TAG, "lcd_render_delta: invalid header");
        return -1;
    }

    size_t pos = 1u;

    /* base_crc — verified by the server before sending this delta; skip here. */
    pos += 4u;

    uint32_t new_crc;
    memcpy(&new_crc, data + pos, sizeof(new_crc));
    pos += 4u;

    uint16_t region_count;
    memcpy(&region_count, data + pos, sizeof(region_count));
    pos += 2u;

    ESP_LOGI(TAG, "Delta: %u region(s), new_crc=0x%08X", region_count, (unsigned)new_crc);

    for (uint16_t i = 0u; i < region_count; i++) {
        /* Region header: x(2) + y(2) + w(2) + h(2) + comp_size(4) = 12 bytes */
        if (pos + 12u > len) {
            ESP_LOGE(TAG, "Delta region %u: header truncated", i);
            return -1;
        }

        uint16_t rx, ry, rw, rh;
        uint32_t comp_size;
        memcpy(&rx,        data + pos, 2u); pos += 2u;
        memcpy(&ry,        data + pos, 2u); pos += 2u;
        memcpy(&rw,        data + pos, 2u); pos += 2u;
        memcpy(&rh,        data + pos, 2u); pos += 2u;
        memcpy(&comp_size, data + pos, 4u); pos += 4u;

        if (pos + comp_size > len) {
            ESP_LOGE(TAG, "Delta region %u: comp_size=%u overruns payload",
                     i, (unsigned)comp_size);
            return -1;
        }

        uint32_t pixel_bytes = (uint32_t)rw * (uint32_t)rh * 2u;

        if (pixel_bytes > (uint32_t)sizeof(s_linebuf)) {
            /* Region too large for the DMA buffer — skip; display shows stale tile. */
            ESP_LOGW(TAG, "Delta region %u: pixel_bytes=%u > linebuf=%u — skipping",
                     i, (unsigned)pixel_bytes, (unsigned)sizeof(s_linebuf));
            pos += comp_size;
            continue;
        }

        int dec = lz4_block_decompress(data + pos, (int)comp_size,
                                        s_linebuf, (int)sizeof(s_linebuf));
        pos += comp_size;

        if (dec != (int)pixel_bytes) {
            ESP_LOGE(TAG, "Delta region %u: decomp=%d expected=%u — skipping",
                     i, dec, (unsigned)pixel_bytes);
            continue;
        }

        /* Write only the changed region to LCD GRAM. */
#ifdef _ILI9486_LCD
        /* ILI9486: raw 16-bit SPI path */
        ili9486_blit((int)rx, (int)ry,
                     (int)(rx + rw), (int)(ry + rh),
                     s_linebuf, (size_t)pixel_bytes);
#else
        ESP_ERROR_CHECK(esp_lcd_panel_io_tx_param(io_handle, LCD_CMD_CASET,
            (uint8_t[]){
                (rx >> 8) & 0xFF,
                rx & 0xFF,
                ((rx + rw - 1u) >> 8) & 0xFF,
                (rx + rw - 1u) & 0xFF,
            }, 4));

        ESP_ERROR_CHECK(esp_lcd_panel_io_tx_param(io_handle, LCD_CMD_RASET,
            (uint8_t[]){
                (ry >> 8) & 0xFF,
                ry & 0xFF,
                ((ry + rh - 1u) >> 8) & 0xFF,
                (ry + rh - 1u) & 0xFF,
            }, 4));

        ESP_ERROR_CHECK(esp_lcd_panel_io_tx_color(io_handle, LCD_CMD_RAMWR,
                                                   s_linebuf, (int)pixel_bytes));
        ESP_ERROR_CHECK(esp_lcd_panel_io_tx_param(io_handle, 0x00, NULL, 0));
#endif
    }

    if (out_new_crc) {
        *out_new_crc = new_crc;
    }
    return 0;
}

int lcd_render_from_flash(const esp_partition_t *partition,
                           uint32_t comp_size,
                           uint32_t *out_crc)
{
    if (!partition || comp_size == 0) {
        ESP_LOGE(TAG, "lcd_render_from_flash: no partition or zero size");
        return -1;
    }

    /* Memory-map the written region.  The MMU maps in 64 KB pages so the
     * actual mapped region may be slightly larger than comp_size. */
    esp_partition_mmap_handle_t mmap_handle;
    const void *mapped_ptr = NULL;

    esp_err_t err = esp_partition_mmap(partition, 0, comp_size,
                                        ESP_PARTITION_MMAP_DATA,
                                        &mapped_ptr, &mmap_handle);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "esp_partition_mmap failed: %s", esp_err_to_name(err));
        return -1;
    }

    const uint8_t *comp = (const uint8_t *)mapped_ptr;
    int rc = -1;

    ESP_LOGI(TAG, "Flash render: %u bytes, flag=0x%02X", (unsigned)comp_size, comp[0]);

    if (comp[0] == FRAME_FLAG_LZ4_PALETTE) {
        rc = lcd_render_lz4(comp, comp_size);
    } else if (comp[0] == FRAME_FLAG_PALETTE) {
        rc = lcd_render_palette(comp, comp_size);
    } else if (comp[0] == FRAME_FLAG_RAW) {
        /* Raw flash render: read sequentially from mmap'd pointer into s_linebuf. */
        const uint8_t *pixels = comp + 1; /* skip flag byte */
        rc = 0;
        for (int y = 0; y < LCD_V_RES && rc == 0; y += LCD_H_LINES) {
            memcpy(s_linebuf,
                   pixels + (uint32_t)y * LCD_H_RES * sizeof(uint16_t),
                   LCD_BUFFER_SIZE);
            send_strip(y, s_linebuf);
        }
    } else {
        ESP_LOGE(TAG, "lcd_render_from_flash: unsupported flag 0x%02X", comp[0]);
    }

    if (rc == 0 && out_crc) {
        *out_crc = esp_rom_crc32_le(0, comp, comp_size);
    }

    esp_partition_munmap(mmap_handle);
    return rc;
}
