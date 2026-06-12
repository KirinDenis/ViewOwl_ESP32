#include <stdlib.h>
#include <string.h>
#include <sys/cdefs.h>

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_lcd_panel_interface.h"
#include "esp_lcd_panel_ops.h"
#include "esp_lcd_panel_commands.h"
#include "driver/gpio.h"
#include "driver/spi_master.h"
#include "esp_log.h"
#include "esp_check.h"

#include "esp_lcd_ili9486.h"

static const char *TAG = "ili9486";

/* ── MADCTL register bit masks ───────────────────────────────────────────── */
#define ILI9486_MADCTL_MY   0x80u   /* row address order */
#define ILI9486_MADCTL_MX   0x40u   /* column address order */
#define ILI9486_MADCTL_MV   0x20u   /* row/column exchange */
#define ILI9486_MADCTL_BGR  0x08u   /* BGR colour order */

/* ── Register addresses ──────────────────────────────────────────────────── */
#define ILI9486_CMD_SLPOUT   0x11
#define ILI9486_CMD_INVOFF   0x20
#define ILI9486_CMD_INVON    0x21
#define ILI9486_CMD_DISPON   0x29
#define ILI9486_CMD_DISPOFF  0x28
#define ILI9486_CMD_CASET    0x2A
#define ILI9486_CMD_RASET    0x2B
#define ILI9486_CMD_RAMWR    0x2C
#define ILI9486_CMD_MADCTL   0x36
#define ILI9486_CMD_COLMOD   0x3A
#define ILI9486_CMD_PWCTRL3  0xC2
#define ILI9486_CMD_VCMPCTL  0xC5
#define ILI9486_CMD_PGAMCTRL 0xE0
#define ILI9486_CMD_NGAMCTRL 0xE1

/* ── Module-level SPI state ──────────────────────────────────────────────── */
/*
 * These three statics are set once by esp_lcd_new_panel_ili9486() and then
 * used by every low-level primitive below.  A single-instance design is fine
 * because the firmware drives exactly one ILI9486 panel.
 */
static spi_device_handle_t s_spi    = NULL;
static int                 s_dc_gpio = -1;
static int                 s_cs_gpio = -1;

/* ── Panel struct ────────────────────────────────────────────────────────── */

typedef struct {
    esp_lcd_panel_t base;
    int     reset_gpio_num;
    int     dc_gpio_num;    /* mirrors s_dc_gpio — kept for gpio_reset_pin in del */
    int     cs_gpio_num;    /* mirrors s_cs_gpio — kept for gpio_reset_pin in del */
    int     x_gap;
    int     y_gap;
    uint8_t madctl_val;
    bool    invert_color;
    bool    disp_on;
} ili9486_panel_t;

/* ── GPIO helpers ────────────────────────────────────────────────────────── */

static inline void cs_low(void)  { gpio_set_level((gpio_num_t)s_cs_gpio, 0); }
static inline void cs_high(void) { gpio_set_level((gpio_num_t)s_cs_gpio, 1); }
static inline void dc_cmd(void)  { gpio_set_level((gpio_num_t)s_dc_gpio, 0); }
static inline void dc_dat(void)  { gpio_set_level((gpio_num_t)s_dc_gpio, 1); }

/* ── Low-level SPI primitives ─────────────────────────────────────────────── */

/**
 * @brief Send one 16-bit word via the SPI FIFO (MSB first).
 *
 * The 74HC245 level-shifter buffers on the Waveshare RPi LCD (A) V3 require
 * a full 16-clock SPI cycle to latch each byte.  Every command and data byte
 * is therefore padded to 16 bits: real byte in the HIGH position, 0x00 in
 * the LOW position.  (This is exactly what TFT_eSPI does for RPI_DISPLAY_TYPE.)
 *
 * ESP-IDF SPI FIFO byte-order trap: with SPI_TRANS_USE_TXDATA the HAL packs
 * tx_data[0] into W0[7:0] which is sent LAST by MSB-first hardware, and
 * tx_data[1] into W0[15:8] which is sent FIRST.  The intended high byte must
 * therefore go into tx_data[1], not tx_data[0].
 */
static void spi_tx16(uint16_t word)
{
    spi_transaction_t t = {
        .length = 16,
        .flags  = SPI_TRANS_USE_TXDATA,
    };
    /* low byte  → W0[7:0]  → sent second (padding 0x00 for commands, lo byte for data) */
    t.tx_data[0] =  word       & 0xFFu;
    /* high byte → W0[15:8] → sent first  (real command or data byte) */
    t.tx_data[1] = (word >> 8) & 0xFFu;
    spi_device_polling_transmit(s_spi, &t);
}

/**
 * @brief Send a command byte (DC=LOW, own CS cycle per byte).
 *
 * Matches TFT_eSPI writecommand(): CS is toggled LOW→HIGH around each byte.
 * Keeping CS asserted across multiple bytes causes incorrect panel behavior.
 */
static void write_cmd(uint8_t cmd)
{
    cs_low();
    dc_cmd();
    spi_tx16((uint16_t)cmd << 8);   /* e.g. 0x2A → send 0x2A00 */
    dc_dat();   /* leave DC HIGH; data bytes follow with DC already high */
    cs_high();
}

/**
 * @brief Send a data byte (DC=HIGH, own CS cycle per byte).
 *
 * Matches TFT_eSPI writedata().
 */
static void write_dat(uint8_t dat)
{
    cs_low();
    /* DC stays HIGH — set by the preceding write_cmd() call */
    spi_tx16((uint16_t)dat << 8);   /* e.g. 0x55 → send 0x5500 */
    cs_high();
}

/**
 * @brief Send a bulk DMA transfer (pixel data).
 *
 * DMA sends bytes in memory order — no byte reversal.  Caller must supply
 * pixels packed as {hi_byte, lo_byte} per pixel (big-endian per pixel), which
 * matches both the server's BGR565 .bin format and the CPU's uint16_t
 * little-endian storage when colours are defined in swapped form.
 *
 * CS must be asserted (LOW) by the caller before this call and deasserted
 * (HIGH) after.
 */
static void spi_tx_bulk(const uint8_t *data, size_t len_bytes)
{
    spi_transaction_t t = {
        .length    = len_bytes * 8u,
        .tx_buffer = data,
    };
    spi_device_polling_transmit(s_spi, &t);
}

/* ── Public blit ─────────────────────────────────────────────────────────── */

/**
 * @brief Set address window and DMA-blast pixel data to the ILI9486.
 *
 * Used by lcd_render.c (send_strip, lcd_render_delta) and lcd_text.c (blit)
 * in place of the esp_lcd_panel_io_tx_param / tx_color calls that require
 * an 8-bit io_handle.
 *
 * CS toggles per byte during the address-window phase (write_cmd/write_dat),
 * then CS is held LOW for the entire DMA pixel burst — matching TFT_eSPI's
 * pushPixels() / pushBlock() protocol.
 */
void ili9486_blit(int x1, int y1, int x2, int y2,
                  const uint8_t *pixels, size_t len)
{
    /* Column address set */
    write_cmd(ILI9486_CMD_CASET);
    write_dat((uint8_t)((x1 >> 8) & 0xFF));
    write_dat((uint8_t)(x1 & 0xFF));
    write_dat((uint8_t)(((x2 - 1) >> 8) & 0xFF));
    write_dat((uint8_t)((x2 - 1) & 0xFF));

    /* Row address set */
    write_cmd(ILI9486_CMD_RASET);
    write_dat((uint8_t)((y1 >> 8) & 0xFF));
    write_dat((uint8_t)(y1 & 0xFF));
    write_dat((uint8_t)(((y2 - 1) >> 8) & 0xFF));
    write_dat((uint8_t)((y2 - 1) & 0xFF));

    /* Memory Write — write_cmd() leaves CS HIGH after this */
    write_cmd(ILI9486_CMD_RAMWR);

    /* Re-assert CS for the pixel DMA burst */
    cs_low();
    spi_tx_bulk(pixels, len);
    cs_high();
}

/* ── Forward declarations ────────────────────────────────────────────────── */

static esp_err_t panel_ili9486_del(esp_lcd_panel_t *panel);
static esp_err_t panel_ili9486_reset(esp_lcd_panel_t *panel);
static esp_err_t panel_ili9486_init(esp_lcd_panel_t *panel);
static esp_err_t panel_ili9486_draw_bitmap(esp_lcd_panel_t *panel,
                                            int x_start, int y_start,
                                            int x_end, int y_end,
                                            const void *color_data);
static esp_err_t panel_ili9486_invert_color(esp_lcd_panel_t *panel, bool invert);
static esp_err_t panel_ili9486_mirror(esp_lcd_panel_t *panel,
                                       bool mirror_x, bool mirror_y);
static esp_err_t panel_ili9486_swap_xy(esp_lcd_panel_t *panel, bool swap_axes);
static esp_err_t panel_ili9486_set_gap(esp_lcd_panel_t *panel, int x_gap, int y_gap);
static esp_err_t panel_ili9486_disp_on_off(esp_lcd_panel_t *panel, bool on_off);

/* ── Constructor ─────────────────────────────────────────────────────────── */

esp_err_t esp_lcd_new_panel_ili9486(spi_device_handle_t spi,
                                     int dc_gpio_num,
                                     int cs_gpio_num,
                                     int rst_gpio_num,
                                     bool bgr_order,
                                     esp_lcd_panel_handle_t *ret_panel)
{
    ESP_RETURN_ON_FALSE(spi && ret_panel, ESP_ERR_INVALID_ARG, TAG, "invalid argument");

    ili9486_panel_t *ili9486 = calloc(1, sizeof(ili9486_panel_t));
    ESP_RETURN_ON_FALSE(ili9486, ESP_ERR_NO_MEM, TAG, "no mem for ili9486 panel");

    /* DC GPIO — idle HIGH (data mode) */
    {
        gpio_config_t io_conf = {
            .mode         = GPIO_MODE_OUTPUT,
            .pin_bit_mask = 1ULL << (uint32_t)dc_gpio_num,
        };
        ESP_RETURN_ON_ERROR(gpio_config(&io_conf), TAG, "DC GPIO config failed");
        gpio_set_level((gpio_num_t)dc_gpio_num, 1);
    }

    /* CS GPIO — idle HIGH (deasserted) */
    {
        gpio_config_t io_conf = {
            .mode         = GPIO_MODE_OUTPUT,
            .pin_bit_mask = 1ULL << (uint32_t)cs_gpio_num,
        };
        ESP_RETURN_ON_ERROR(gpio_config(&io_conf), TAG, "CS GPIO config failed");
        gpio_set_level((gpio_num_t)cs_gpio_num, 1);
    }

    /* RST GPIO (optional) */
    if (rst_gpio_num >= 0) {
        gpio_config_t io_conf = {
            .mode         = GPIO_MODE_OUTPUT,
            .pin_bit_mask = 1ULL << (uint32_t)rst_gpio_num,
        };
        ESP_RETURN_ON_ERROR(gpio_config(&io_conf), TAG, "RST GPIO config failed");
    }

    /* Populate module-level statics used by write_cmd / write_dat / spi_tx_bulk */
    s_spi     = spi;
    s_dc_gpio = dc_gpio_num;
    s_cs_gpio = cs_gpio_num;

    /* Populate the panel struct */
    ili9486->reset_gpio_num = rst_gpio_num;
    ili9486->dc_gpio_num    = dc_gpio_num;
    ili9486->cs_gpio_num    = cs_gpio_num;
    ili9486->madctl_val     = bgr_order ? ILI9486_MADCTL_BGR : 0u;
    ili9486->invert_color   = false;
    ili9486->disp_on        = false;
    ili9486->x_gap          = 0;
    ili9486->y_gap          = 0;

    /* Vtable */
    ili9486->base.del          = panel_ili9486_del;
    ili9486->base.reset        = panel_ili9486_reset;
    ili9486->base.init         = panel_ili9486_init;
    ili9486->base.draw_bitmap  = panel_ili9486_draw_bitmap;
    ili9486->base.invert_color = panel_ili9486_invert_color;
    ili9486->base.mirror       = panel_ili9486_mirror;
    ili9486->base.swap_xy      = panel_ili9486_swap_xy;
    ili9486->base.set_gap      = panel_ili9486_set_gap;
    ili9486->base.disp_on_off  = panel_ili9486_disp_on_off;

    *ret_panel = &ili9486->base;
    ESP_LOGI(TAG, "ILI9486 panel created (dc=%d, cs=%d, rst=%d)",
             dc_gpio_num, cs_gpio_num, rst_gpio_num);
    return ESP_OK;
}

/* ── Panel operations ────────────────────────────────────────────────────── */

static esp_err_t panel_ili9486_del(esp_lcd_panel_t *panel)
{
    ili9486_panel_t *ili9486 = __containerof(panel, ili9486_panel_t, base);
    if (ili9486->reset_gpio_num >= 0) {
        gpio_reset_pin((gpio_num_t)ili9486->reset_gpio_num);
    }
    gpio_reset_pin((gpio_num_t)ili9486->dc_gpio_num);
    gpio_reset_pin((gpio_num_t)ili9486->cs_gpio_num);
    s_spi     = NULL;
    s_dc_gpio = -1;
    s_cs_gpio = -1;
    free(ili9486);
    ESP_LOGI(TAG, "ILI9486 panel deleted");
    return ESP_OK;
}

static esp_err_t panel_ili9486_reset(esp_lcd_panel_t *panel)
{
    ili9486_panel_t *ili9486 = __containerof(panel, ili9486_panel_t, base);

    if (ili9486->reset_gpio_num >= 0) {
        /* Hardware reset: pulse LOW then restore HIGH */
        gpio_set_level((gpio_num_t)ili9486->reset_gpio_num, 0);
        vTaskDelay(pdMS_TO_TICKS(10));
        gpio_set_level((gpio_num_t)ili9486->reset_gpio_num, 1);
        vTaskDelay(pdMS_TO_TICKS(120));
    } else {
        /* Software reset (0x01) when no RST pin is wired */
        write_cmd(0x01);
        vTaskDelay(pdMS_TO_TICKS(120));
    }
    return ESP_OK;
}

static esp_err_t panel_ili9486_init(esp_lcd_panel_t *panel)
{
    ili9486_panel_t *ili9486 = __containerof(panel, ili9486_panel_t, base);

    /* Sleep out — mandatory first command after reset */
    write_cmd(ILI9486_CMD_SLPOUT);
    vTaskDelay(pdMS_TO_TICKS(120));

    /* COLMOD: 16-bit colour (RGB565).
     * RPi-compatible panels require 0x55; 0x66 (18-bit) produces colour corruption. */
    write_cmd(ILI9486_CMD_COLMOD);
    write_dat(0x55);

    /* Power Control 3 */
    write_cmd(ILI9486_CMD_PWCTRL3);
    write_dat(0x44);

    /* VCOM Control */
    write_cmd(ILI9486_CMD_VCMPCTL);
    write_dat(0x00); write_dat(0x00); write_dat(0x00); write_dat(0x00);

    /* Positive Gamma Correction (from TFT_eSPI ILI9486_Init.h) */
    write_cmd(ILI9486_CMD_PGAMCTRL);
    write_dat(0x0F); write_dat(0x1F); write_dat(0x1C); write_dat(0x0C); write_dat(0x0F);
    write_dat(0x08); write_dat(0x48); write_dat(0x98); write_dat(0x37); write_dat(0x0A);
    write_dat(0x13); write_dat(0x04); write_dat(0x11); write_dat(0x0D); write_dat(0x00);

    /* Negative Gamma Correction */
    write_cmd(ILI9486_CMD_NGAMCTRL);
    write_dat(0x0F); write_dat(0x32); write_dat(0x2E); write_dat(0x0B); write_dat(0x0D);
    write_dat(0x05); write_dat(0x47); write_dat(0x75); write_dat(0x37); write_dat(0x06);
    write_dat(0x10); write_dat(0x03); write_dat(0x24); write_dat(0x20); write_dat(0x00);

    /* Display Inversion OFF — required for Waveshare RPi LCD (A) variant.
     * The (B) variant needs INVON (0x21) instead. */
    write_cmd(ILI9486_CMD_INVOFF);

    /* Memory Access Control — initial MADCTL (BGR bit set in constructor) */
    write_cmd(ILI9486_CMD_MADCTL);
    write_dat(ili9486->madctl_val);

    /* Display ON */
    write_cmd(ILI9486_CMD_DISPON);
    vTaskDelay(pdMS_TO_TICKS(150));

    ili9486->disp_on = true;
    ESP_LOGI(TAG, "ILI9486 init done (MADCTL=0x%02X)", ili9486->madctl_val);
    return ESP_OK;
}

static esp_err_t panel_ili9486_draw_bitmap(esp_lcd_panel_t *panel,
                                            int x_start, int y_start,
                                            int x_end, int y_end,
                                            const void *color_data)
{
    ili9486_panel_t *ili9486 = __containerof(panel, ili9486_panel_t, base);

    x_start += ili9486->x_gap;
    x_end   += ili9486->x_gap;
    y_start += ili9486->y_gap;
    y_end   += ili9486->y_gap;

    size_t len = (size_t)(x_end - x_start) * (size_t)(y_end - y_start) * sizeof(uint16_t);
    ili9486_blit(x_start, y_start, x_end, y_end, (const uint8_t *)color_data, len);
    return ESP_OK;
}

static esp_err_t panel_ili9486_invert_color(esp_lcd_panel_t *panel, bool invert)
{
    ili9486_panel_t *ili9486 = __containerof(panel, ili9486_panel_t, base);
    write_cmd(invert ? ILI9486_CMD_INVON : ILI9486_CMD_INVOFF);
    ili9486->invert_color = invert;
    return ESP_OK;
}

static esp_err_t panel_ili9486_mirror(esp_lcd_panel_t *panel,
                                       bool mirror_x, bool mirror_y)
{
    ili9486_panel_t *ili9486 = __containerof(panel, ili9486_panel_t, base);

    if (mirror_x) {
        ili9486->madctl_val |= ILI9486_MADCTL_MX;
    } else {
        ili9486->madctl_val &= (uint8_t)~ILI9486_MADCTL_MX;
    }
    if (mirror_y) {
        ili9486->madctl_val |= ILI9486_MADCTL_MY;
    } else {
        ili9486->madctl_val &= (uint8_t)~ILI9486_MADCTL_MY;
    }

    write_cmd(ILI9486_CMD_MADCTL);
    write_dat(ili9486->madctl_val);
    return ESP_OK;
}

static esp_err_t panel_ili9486_swap_xy(esp_lcd_panel_t *panel, bool swap_axes)
{
    ili9486_panel_t *ili9486 = __containerof(panel, ili9486_panel_t, base);

    if (swap_axes) {
        ili9486->madctl_val |= ILI9486_MADCTL_MV;
    } else {
        ili9486->madctl_val &= (uint8_t)~ILI9486_MADCTL_MV;
    }

    write_cmd(ILI9486_CMD_MADCTL);
    write_dat(ili9486->madctl_val);
    return ESP_OK;
}

static esp_err_t panel_ili9486_set_gap(esp_lcd_panel_t *panel, int x_gap, int y_gap)
{
    ili9486_panel_t *ili9486 = __containerof(panel, ili9486_panel_t, base);
    ili9486->x_gap = x_gap;
    ili9486->y_gap = y_gap;
    return ESP_OK;
}

static esp_err_t panel_ili9486_disp_on_off(esp_lcd_panel_t *panel, bool on_off)
{
    ili9486_panel_t *ili9486 = __containerof(panel, ili9486_panel_t, base);
    write_cmd(on_off ? ILI9486_CMD_DISPON : ILI9486_CMD_DISPOFF);
    ili9486->disp_on = on_off;
    return ESP_OK;
}
