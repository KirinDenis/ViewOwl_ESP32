/**
 * @file lcd_gc9a01.c
 * @brief GC9A01 round display driver - Elecrow CrowPanel 1.28" (ESP32-C3).
 *
 * Ported from the proven diagnostic (diagnostics/gc9a01/main/diag_gc9a01.c),
 * which lit and rendered correctly on the first flash. See the memory skill
 * skill_gc9a01_crowpanel for the full hardware notes.
 */

#include <string.h>
#include <math.h>

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "driver/spi_master.h"
#include "driver/gpio.h"
#include "driver/i2c_master.h"
#include "esp_log.h"

#include "config.h"
#include "lcd_gc9a01.h"

static const char *TAG = "lcd_gc9a01";

/* -- Board wiring (CrowPanel 1.28" round, ESP32-C3) -------------------------- */
#define PIN_SCLK    6
#define PIN_MOSI    7
#define PIN_MISO   -1
#define PIN_CS     10
#define PIN_DC      2

#define I2C_SDA     4
#define I2C_SCL     5
#define IOEX_ADDR   0x43          /* PI4IOE5V6408 I2C GPIO expander */

#define SPI_CLOCK_HZ  (40 * 1000 * 1000)
/* MV+MX+BGR. 0x28 (MV+BGR) rendered real content mirrored left-right (only visible
 * on asymmetric frames); adding MX (0x40) un-mirrors it. If it comes out vertically
 * flipped instead, use MY (0xA8); both axes = 0xE8. */
#define MADCTL_DEFAULT 0x68

/* PI4IOE5V6408 register map + the output bits we drive */
#define IOEX_REG_DIR      0x03    /* 1 = output                       */
#define IOEX_REG_OUT      0x05    /* 1 = high                         */
#define IOEX_REG_HIZ      0x07    /* 1 = Hi-Z (default 0xFF; clear=drive) */
#define IOEX_REG_PULL_EN  0x0B
#define IOEX_BIT_BL   (1 << 2)    /* backlight */
#define IOEX_BIT_RST  (1 << 4)    /* LCD reset */

#define STRIP_ROWS 20
static __attribute__((aligned(4))) uint8_t s_buf[LCD_H_RES * STRIP_ROWS * 2];

static spi_device_handle_t      s_spi;
static i2c_master_bus_handle_t  s_i2c_bus;
static i2c_master_dev_handle_t  s_ioex;

/* -- I/O expander ------------------------------------------------------------ */

static void ioex_write(uint8_t reg, uint8_t val)
{
    uint8_t buf[2] = { reg, val };
    esp_err_t err = i2c_master_transmit(s_ioex, buf, sizeof(buf), 100);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "ioex_write(0x%02X,0x%02X): %s", reg, val, esp_err_to_name(err));
    }
}

static void panel_power_on(void)
{
    const uint8_t mask = IOEX_BIT_BL | IOEX_BIT_RST;
    ioex_write(IOEX_REG_DIR,     mask);
    ioex_write(IOEX_REG_HIZ,     (uint8_t)~mask);
    ioex_write(IOEX_REG_PULL_EN, 0x00);
    ioex_write(IOEX_REG_OUT, IOEX_BIT_BL);                /* RST low (assert), BL on */
    vTaskDelay(pdMS_TO_TICKS(20));
    ioex_write(IOEX_REG_OUT, IOEX_BIT_BL | IOEX_BIT_RST); /* RST high (release)      */
    vTaskDelay(pdMS_TO_TICKS(120));
}

/* -- SPI primitives (8-bit, manual CS) --------------------------------------- */

static inline void cs_low(void)  { gpio_set_level(PIN_CS, 0); }
static inline void cs_high(void) { gpio_set_level(PIN_CS, 1); }
static inline void dc_cmd(void)  { gpio_set_level(PIN_DC, 0); }
static inline void dc_dat(void)  { gpio_set_level(PIN_DC, 1); }

static void spi_tx8(uint8_t b)
{
    spi_transaction_t t = { .length = 8, .flags = SPI_TRANS_USE_TXDATA };
    t.tx_data[0] = b;
    spi_device_polling_transmit(s_spi, &t);
}

static void spi_tx_bulk(const uint8_t *data, size_t len)
{
    if (len == 0) return;
    spi_transaction_t t = { .length = len * 8, .tx_buffer = data };
    spi_device_polling_transmit(s_spi, &t);
}

static void send_cmd(uint8_t cmd, const uint8_t *data, size_t n)
{
    cs_low();
    dc_cmd();
    spi_tx8(cmd);
    if (n) { dc_dat(); spi_tx_bulk(data, n); }
    cs_high();
}

/* -- GC9A01 init (canonical sequence, BGR + INVON; proven by diagnostic) ----- */

static void gc9a01_panel_init(void)
{
    send_cmd(0xEF, NULL, 0);
    send_cmd(0xEB, (const uint8_t[]){0x14}, 1);
    send_cmd(0xFE, NULL, 0);
    send_cmd(0xEF, NULL, 0);
    send_cmd(0xEB, (const uint8_t[]){0x14}, 1);
    send_cmd(0x84, (const uint8_t[]){0x40}, 1);
    send_cmd(0x85, (const uint8_t[]){0xFF}, 1);
    send_cmd(0x86, (const uint8_t[]){0xFF}, 1);
    send_cmd(0x87, (const uint8_t[]){0xFF}, 1);
    send_cmd(0x88, (const uint8_t[]){0x0A}, 1);
    send_cmd(0x89, (const uint8_t[]){0x21}, 1);
    send_cmd(0x8A, (const uint8_t[]){0x00}, 1);
    send_cmd(0x8B, (const uint8_t[]){0x80}, 1);
    send_cmd(0x8C, (const uint8_t[]){0x01}, 1);
    send_cmd(0x8D, (const uint8_t[]){0x01}, 1);
    send_cmd(0x8E, (const uint8_t[]){0xFF}, 1);
    send_cmd(0x8F, (const uint8_t[]){0xFF}, 1);
    send_cmd(0xB6, (const uint8_t[]){0x00, 0x20}, 2);
    send_cmd(0x36, (const uint8_t[]){MADCTL_DEFAULT}, 1);
    send_cmd(0x3A, (const uint8_t[]){0x05}, 1);
    send_cmd(0x90, (const uint8_t[]){0x08, 0x08, 0x08, 0x08}, 4);
    send_cmd(0xBD, (const uint8_t[]){0x06}, 1);
    send_cmd(0xBC, (const uint8_t[]){0x00}, 1);
    send_cmd(0xFF, (const uint8_t[]){0x60, 0x01, 0x04}, 3);
    send_cmd(0xC3, (const uint8_t[]){0x13}, 1);
    send_cmd(0xC4, (const uint8_t[]){0x13}, 1);
    send_cmd(0xC9, (const uint8_t[]){0x22}, 1);
    send_cmd(0xBE, (const uint8_t[]){0x11}, 1);
    send_cmd(0xE1, (const uint8_t[]){0x10, 0x0E}, 2);
    send_cmd(0xDF, (const uint8_t[]){0x21, 0x0C, 0x02}, 3);
    send_cmd(0xF0, (const uint8_t[]){0x45, 0x09, 0x08, 0x08, 0x26, 0x2A}, 6);
    send_cmd(0xF1, (const uint8_t[]){0x43, 0x70, 0x72, 0x36, 0x37, 0x6F}, 6);
    send_cmd(0xF2, (const uint8_t[]){0x45, 0x09, 0x08, 0x08, 0x26, 0x2A}, 6);
    send_cmd(0xF3, (const uint8_t[]){0x43, 0x70, 0x72, 0x36, 0x37, 0x6F}, 6);
    send_cmd(0xED, (const uint8_t[]){0x1B, 0x0B}, 2);
    send_cmd(0xAE, (const uint8_t[]){0x77}, 1);
    send_cmd(0xCD, (const uint8_t[]){0x63}, 1);
    send_cmd(0x70, (const uint8_t[]){0x07, 0x07, 0x04, 0x0E, 0x0F, 0x09, 0x07, 0x08, 0x03}, 9);
    send_cmd(0xE8, (const uint8_t[]){0x34}, 1);
    send_cmd(0x62, (const uint8_t[]){0x18, 0x0D, 0x71, 0xED, 0x70, 0x70,
                                     0x18, 0x0F, 0x71, 0xEF, 0x70, 0x70}, 12);
    send_cmd(0x63, (const uint8_t[]){0x18, 0x11, 0x71, 0xF1, 0x70, 0x70,
                                     0x18, 0x13, 0x71, 0xF3, 0x70, 0x70}, 12);
    send_cmd(0x64, (const uint8_t[]){0x28, 0x29, 0xF1, 0x01, 0xF1, 0x00, 0x07}, 7);
    send_cmd(0x66, (const uint8_t[]){0x3C, 0x00, 0xCD, 0x67, 0x45, 0x45,
                                     0x10, 0x00, 0x00, 0x00}, 10);
    send_cmd(0x67, (const uint8_t[]){0x00, 0x3C, 0x00, 0x00, 0x00, 0x01,
                                     0x54, 0x10, 0x32, 0x98}, 10);
    send_cmd(0x74, (const uint8_t[]){0x10, 0x85, 0x80, 0x00, 0x00, 0x4E, 0x00}, 7);
    send_cmd(0x98, (const uint8_t[]){0x3E, 0x07}, 2);
    send_cmd(0x35, NULL, 0);                              /* TE on   */
    send_cmd(0x21, NULL, 0);                              /* INVON   */
    send_cmd(0x11, NULL, 0);                              /* SLPOUT  */
    vTaskDelay(pdMS_TO_TICKS(120));
    send_cmd(0x29, NULL, 0);                              /* DISPON  */
    vTaskDelay(pdMS_TO_TICKS(20));
}

/* -- Public draw API --------------------------------------------------------- */

static void set_window(int x1, int y1, int x2, int y2)
{
    send_cmd(0x2A, (const uint8_t[]){ (x1 >> 8) & 0xFF, x1 & 0xFF,
                                      ((x2 - 1) >> 8) & 0xFF, (x2 - 1) & 0xFF }, 4);
    send_cmd(0x2B, (const uint8_t[]){ (y1 >> 8) & 0xFF, y1 & 0xFF,
                                      ((y2 - 1) >> 8) & 0xFF, (y2 - 1) & 0xFF }, 4);
}

void lcd_gc9a01_fill_rect(int x, int y, int w, int h, uint16_t colour)
{
    if (w <= 0 || h <= 0) return;
    uint8_t hi = (uint8_t)(colour >> 8);
    uint8_t lo = (uint8_t)(colour & 0xFF);

    set_window(x, y, x + w, y + h);
    cs_low(); dc_cmd(); spi_tx8(0x2C); dc_dat();          /* RAMWR */

    int done = 0;
    while (done < h) {
        int rows = (h - done > STRIP_ROWS) ? STRIP_ROWS : (h - done);
        int count = w * rows;
        for (int i = 0; i < count; i++) {
            s_buf[i * 2]     = hi;
            s_buf[i * 2 + 1] = lo;
        }
        spi_tx_bulk(s_buf, (size_t)count * 2);
        done += rows;
    }
    cs_high();
}

void lcd_gc9a01_fill(uint16_t colour)
{
    lcd_gc9a01_fill_rect(0, 0, LCD_H_RES, LCD_V_RES, colour);
}

void lcd_gc9a01_fill_circle(int cx, int cy, int r, uint16_t colour)
{
    for (int dy = -r; dy <= r; dy++) {
        int yy = cy + dy;
        if (yy < 0 || yy >= LCD_V_RES) continue;
        int hw = (int)sqrtf((float)(r * r - dy * dy));
        int x = cx - hw, w = 2 * hw + 1;
        if (x < 0) { w += x; x = 0; }
        if (x + w > LCD_H_RES) w = LCD_H_RES - x;
        if (w > 0) lcd_gc9a01_fill_rect(x, yy, w, 1, colour);
    }
}

void lcd_gc9a01_blit(int x, int y, int w, int h, const uint16_t *pixels)
{
    if (w <= 0 || h <= 0) return;
    set_window(x, y, x + w, y + h);
    cs_low(); dc_cmd(); spi_tx8(0x2C); dc_dat();          /* RAMWR */

    int total = w * h, done = 0;
    while (done < total) {
        int chunk = total - done;
        if (chunk > LCD_H_RES * STRIP_ROWS) chunk = LCD_H_RES * STRIP_ROWS;
        for (int i = 0; i < chunk; i++) {
            uint16_t px = pixels[done + i];
            s_buf[i * 2]     = (uint8_t)(px >> 8);
            s_buf[i * 2 + 1] = (uint8_t)(px & 0xFF);
        }
        spi_tx_bulk(s_buf, (size_t)chunk * 2);
        done += chunk;
    }
    cs_high();
}

void lcd_gc9a01_blit_be(int x, int y, int w, int h, const uint8_t *be_pixels)
{
    if (w <= 0 || h <= 0) return;
    set_window(x, y, x + w, y + h);
    cs_low(); dc_cmd(); spi_tx8(0x2C); dc_dat();          /* RAMWR */

    /* Bytes are already wire-order (high byte first) - send them straight through,
     * chunked to the SPI max_transfer_sz (LCD_H_RES * STRIP_ROWS * 2). */
    const size_t maxchunk = (size_t)LCD_H_RES * STRIP_ROWS * 2;
    size_t total = (size_t)w * h * 2;
    size_t done  = 0;
    while (done < total) {
        size_t chunk = total - done;
        if (chunk > maxchunk) chunk = maxchunk;
        spi_tx_bulk(be_pixels + done, chunk);
        done += chunk;
    }
    cs_high();
}

/* -- Init -------------------------------------------------------------------- */

void lcd_gc9a01_init(void)
{
    /* I2C bus + expander -> release reset, backlight on (before SPI). */
    i2c_master_bus_config_t bus_cfg = {
        .i2c_port          = -1,
        .sda_io_num        = I2C_SDA,
        .scl_io_num        = I2C_SCL,
        .clk_source        = I2C_CLK_SRC_DEFAULT,
        .glitch_ignore_cnt = 7,
        .flags.enable_internal_pullup = true,
    };
    ESP_ERROR_CHECK(i2c_new_master_bus(&bus_cfg, &s_i2c_bus));
    i2c_device_config_t dev_cfg = {
        .dev_addr_length = I2C_ADDR_BIT_LEN_7,
        .device_address  = IOEX_ADDR,
        .scl_speed_hz    = 400000,
    };
    ESP_ERROR_CHECK(i2c_master_bus_add_device(s_i2c_bus, &dev_cfg, &s_ioex));
    panel_power_on();

    /* CS / DC GPIO */
    gpio_config_t pin_cfg = {
        .mode         = GPIO_MODE_OUTPUT,
        .pin_bit_mask = (1ULL << PIN_CS) | (1ULL << PIN_DC),
    };
    ESP_ERROR_CHECK(gpio_config(&pin_cfg));
    gpio_set_level(PIN_CS, 1);
    gpio_set_level(PIN_DC, 1);

    /* SPI bus + device (manual CS) */
    spi_bus_config_t bus = {
        .sclk_io_num     = PIN_SCLK,
        .mosi_io_num     = PIN_MOSI,
        .miso_io_num     = PIN_MISO,
        .quadwp_io_num   = -1,
        .quadhd_io_num   = -1,
        .max_transfer_sz = LCD_H_RES * STRIP_ROWS * 2,
    };
    ESP_ERROR_CHECK(spi_bus_initialize(SPI2_HOST, &bus, SPI_DMA_CH_AUTO));
    spi_device_interface_config_t dev = {
        .clock_speed_hz = SPI_CLOCK_HZ,
        .mode           = 0,
        .spics_io_num   = -1,
        .queue_size     = 7,
    };
    ESP_ERROR_CHECK(spi_bus_add_device(SPI2_HOST, &dev, &s_spi));

    gc9a01_panel_init();
    ESP_LOGI(TAG, "GC9A01 ready - %dx%d, MADCTL=0x%02X", LCD_H_RES, LCD_V_RES, MADCTL_DEFAULT);
}
