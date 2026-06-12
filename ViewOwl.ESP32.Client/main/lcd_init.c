
#include "config.h"
#include "esp_timer.h"
#include "esp_lcd_panel_io.h"
#include "esp_lcd_panel_vendor.h"
#include "esp_lcd_panel_ops.h"
#include "esp_lcd_panel_commands.h"
#include "driver/gpio.h"
#include "driver/spi_master.h"
#include "esp_log.h"

#ifdef _ILI9486_LCD
#include "esp_lcd_ili9486.h"
#elif defined(_480x320_LCD)
#include "esp_lcd_st7796.h"
#else
#include "esp_lcd_ili9341.h"
#endif

static const char *TAG = "LCD";

#define LCD_HOST SPI2_HOST
#define LCD_BK_LIGHT_ON_LEVEL 1

#ifdef _ILI9486_LCD
/* ILI9486 RPi-compatible 3.5" 480×320 — Waveshare RPi LCD (A) V3 pinout.
 * Driven via raw 16-bit SPI (manual CS) to satisfy the 74HC245 level-shifter
 * buffers.  esp_lcd with lcd_cmd_bits=8 does NOT work with this display.
 * SPI clock limited to 20 MHz per RPi-compatible panel specification.
 * Backlight is wired permanently to power — no GPIO control needed. */
#define LCD_PIXEL_CLOCK_HZ  (20 * 1000 * 1000)
#define PIN_NUM_SCLK        18
#define PIN_NUM_MOSI        23
#define PIN_NUM_MISO        19
#define PIN_NUM_LCD_DC       2
#define PIN_NUM_LCD_RST      4
#define PIN_NUM_LCD_CS      15
#define PIN_NUM_BK_LIGHT    -1
#else
/* ILI9341 (320×240) and ST7796 (480×320) share the same SPI wiring.
 * SPI clock: 50 MHz — full 480×320 BGR565 frame transfers in ~49 ms. */
#define LCD_PIXEL_CLOCK_HZ  (50 * 1000 * 1000)
#define PIN_NUM_SCLK        14
#define PIN_NUM_MOSI        13
#define PIN_NUM_MISO        12
#define PIN_NUM_LCD_DC       2
#define PIN_NUM_LCD_RST     -1
#define PIN_NUM_LCD_CS      15
#define PIN_NUM_BK_LIGHT    LIGHT_PIN
#endif

#define PIN_NUM_TOUCH_CS 33

/* Bit number used to represent command and parameter */
#define LCD_CMD_BITS   8
#define LCD_PARAM_BITS 8

esp_lcd_panel_handle_t panel_handle = NULL;
/* io_handle is NULL for the ILI9486 build variant (raw SPI path) and
 * valid for ILI9341 / ST7796 (esp_lcd path).  lcd_render.c and lcd_text.c
 * guard their io_handle usage with #ifdef _ILI9486_LCD. */
esp_lcd_panel_io_handle_t io_handle = NULL;

void lcd_init(void)
{
#if PIN_NUM_BK_LIGHT >= 0
    gpio_config_t bk_gpio_config = {
        .mode = GPIO_MODE_OUTPUT,
        .pin_bit_mask = 1ULL << PIN_NUM_BK_LIGHT,
    };
    ESP_ERROR_CHECK(gpio_config(&bk_gpio_config));
#endif

    ESP_LOGI(TAG, "Initialize SPI bus");
    spi_bus_config_t buscfg = {
        .sclk_io_num     = PIN_NUM_SCLK,
        .mosi_io_num     = PIN_NUM_MOSI,
        .miso_io_num     = PIN_NUM_MISO,
        .quadwp_io_num   = -1,
        .quadhd_io_num   = -1,
        .max_transfer_sz = LCD_BUFFER_SIZE,
    };
    ESP_ERROR_CHECK(spi_bus_initialize(LCD_HOST, &buscfg, SPI_DMA_CH_AUTO));

#ifdef _ILI9486_LCD
    /* ILI9486: raw SPI device with manual CS.
     * The 74HC245 buffers require 16-bit transactions per byte — the standard
     * esp_lcd panel IO (lcd_cmd_bits=8) does not latch commands correctly.
     * DC and CS are managed manually by esp_lcd_ili9486.c. */
    ESP_LOGI(TAG, "Install esp_lcd_ili9486 panel driver (raw SPI, 20 MHz)");
    spi_device_handle_t ili9486_spi = NULL;
    spi_device_interface_config_t dev_cfg = {
        .clock_speed_hz = LCD_PIXEL_CLOCK_HZ,
        .mode           = 0,
        .spics_io_num   = -1,   /* manual CS — driver owns the CS GPIO */
        .queue_size     = 1,
    };
    ESP_ERROR_CHECK(spi_bus_add_device(LCD_HOST, &dev_cfg, &ili9486_spi));
    ESP_ERROR_CHECK(esp_lcd_new_panel_ili9486(ili9486_spi,
                                               PIN_NUM_LCD_DC,
                                               PIN_NUM_LCD_CS,
                                               PIN_NUM_LCD_RST,
                                               true,   /* BGR order */
                                               &panel_handle));
#else
    /* ILI9341 / ST7796: standard esp_lcd panel IO with hardware CS */
    ESP_LOGI(TAG, "Install panel IO");
    esp_lcd_panel_io_spi_config_t io_config = {
        .dc_gpio_num       = PIN_NUM_LCD_DC,
        .cs_gpio_num       = PIN_NUM_LCD_CS,
        .pclk_hz           = LCD_PIXEL_CLOCK_HZ,
        .lcd_cmd_bits      = LCD_CMD_BITS,
        .lcd_param_bits    = LCD_PARAM_BITS,
        .spi_mode          = 0,
        .trans_queue_depth = 1,
        .on_color_trans_done = NULL,
    };
    ESP_ERROR_CHECK(esp_lcd_new_panel_io_spi((esp_lcd_spi_bus_handle_t)LCD_HOST,
                                              &io_config, &io_handle));

    esp_lcd_panel_dev_config_t panel_config = {
        .reset_gpio_num = PIN_NUM_LCD_RST,
        .rgb_ele_order  = LCD_RGB_ELEMENT_ORDER_BGR,
        .bits_per_pixel = 16,
    };

#if defined(_480x320_LCD)
    ESP_LOGI(TAG, "Install esp_lcd_st7796 panel driver");
    ESP_ERROR_CHECK(esp_lcd_new_panel_st7796(io_handle, &panel_config, &panel_handle));
#else
    ESP_LOGI(TAG, "Install esp_lcd_ili9341 panel driver");
    ESP_ERROR_CHECK(esp_lcd_new_panel_ili9341(io_handle, &panel_config, &panel_handle));
#endif

#endif /* _ILI9486_LCD */

    ESP_ERROR_CHECK(esp_lcd_panel_reset(panel_handle));
    ESP_ERROR_CHECK(esp_lcd_panel_init(panel_handle));
    esp_lcd_panel_swap_xy(panel_handle, true);
#ifndef _ILI9486_LCD
    /* ILI9341 / ST7796: portrait-native panels need MY+MX to reach correct landscape
     * scan direction (MADCTL=0xE8).  ILI9486 RPi-compatible panels are landscape-native
     * in the MV-only configuration (MADCTL=0x28, matches TFT_eSPI rotation=1). */
    ESP_ERROR_CHECK(esp_lcd_panel_mirror(panel_handle, true, true));
#endif
    esp_lcd_panel_invert_color(panel_handle, false);
    ESP_ERROR_CHECK(esp_lcd_panel_disp_on_off(panel_handle, true));

#if PIN_NUM_BK_LIGHT >= 0
    ESP_LOGI(TAG, "Turn on LCD backlight");
    gpio_set_level(PIN_NUM_BK_LIGHT, LCD_BK_LIGHT_ON_LEVEL);
#else
    ESP_LOGI(TAG, "LCD backlight: always-on (wired to power, no GPIO)");
#endif
}
