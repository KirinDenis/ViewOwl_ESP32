/**
 * @file epd_4in2.c
 * @brief 400x300 SSD1680-family e-paper driver (bit-banged SPI), ESP-IDF.
 */
#include "epd_4in2.h"

#include "driver/gpio.h"
#include "esp_rom_sys.h"
#include <stdbool.h>
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

static const char *TAG = "epd";

/* -- Pins (Denys's proven wiring; ESP32-S3) --------------------------------- */
#define EPD_PIN_SCK   12
#define EPD_PIN_MOSI  11
#define EPD_PIN_RES   47
#define EPD_PIN_DC    46
#define EPD_PIN_CS    45
#define EPD_PIN_BUSY  48
#define EPD_PIN_PWR   7    /* power-enable: must be HIGH or the panel is unpowered */

/* Half-clock delay for the bit-bang. The panel is happy well below 1 MHz, so a
 * conservative ~1 us keeps setup/hold comfortable on the first bring-up. */
#define EPD_SPI_DELAY_US 1

/* BUSY is active-HIGH (high = controller busy). Bound the wait so a mis-wired BUSY
 * line fails loudly instead of hanging the bring-up forever. */
#define EPD_BUSY_TIMEOUT_MS 10000

/* -- Low-level SPI bit-bang ------------------------------------------------- */

static void epd_wr_bus(uint8_t dat)
{
    gpio_set_level(EPD_PIN_CS, 0);
    for (int i = 0; i < 8; i++) {
        gpio_set_level(EPD_PIN_SCK, 0);
        gpio_set_level(EPD_PIN_MOSI, (dat & 0x80) ? 1 : 0);
        esp_rom_delay_us(EPD_SPI_DELAY_US);
        gpio_set_level(EPD_PIN_SCK, 1);          /* sampled on the rising edge */
        esp_rom_delay_us(EPD_SPI_DELAY_US);
        dat <<= 1;
    }
    gpio_set_level(EPD_PIN_CS, 1);
}

static void epd_wr_reg(uint8_t reg)
{
    gpio_set_level(EPD_PIN_DC, 0);   /* command */
    epd_wr_bus(reg);
    gpio_set_level(EPD_PIN_DC, 1);
}

static void epd_wr_data(uint8_t dat)
{
    gpio_set_level(EPD_PIN_DC, 1);   /* data */
    epd_wr_bus(dat);
}

/* -- Housekeeping ----------------------------------------------------------- */

static void epd_reset(void)
{
    gpio_set_level(EPD_PIN_RES, 1);
    vTaskDelay(pdMS_TO_TICKS(100));
    gpio_set_level(EPD_PIN_RES, 0);
    vTaskDelay(pdMS_TO_TICKS(10));
    gpio_set_level(EPD_PIN_RES, 1);
    vTaskDelay(pdMS_TO_TICKS(10));
}

static void epd_wait_busy(void)
{
    int waited = 0;
    while (gpio_get_level(EPD_PIN_BUSY) == 1) {
        vTaskDelay(pdMS_TO_TICKS(10));
        waited += 10;
        if (waited >= EPD_BUSY_TIMEOUT_MS) {
            ESP_LOGE(TAG, "BUSY stuck HIGH for %d ms - check the BUSY wiring (GPIO%d)",
                     waited, EPD_PIN_BUSY);
            return;
        }
    }
}

/* RAM address window (X is byte-addressed, Y is per-pixel 16-bit). */
static void epd_set_window(uint16_t xs, uint16_t ys, uint16_t xe, uint16_t ye)
{
    epd_wr_reg(0x44);                       /* SET_RAM_X_ADDRESS_START_END */
    epd_wr_data((xs >> 3) & 0xFF);
    epd_wr_data((xe >> 3) & 0xFF);

    epd_wr_reg(0x45);                       /* SET_RAM_Y_ADDRESS_START_END */
    epd_wr_data(ys & 0xFF);
    epd_wr_data((ys >> 8) & 0xFF);
    epd_wr_data(ye & 0xFF);
    epd_wr_data((ye >> 8) & 0xFF);
}

static void epd_set_cursor(uint16_t xs, uint16_t ys)
{
    epd_wr_reg(0x4E);                       /* SET_RAM_X_ADDRESS_COUNTER */
    epd_wr_data(xs & 0xFF);

    epd_wr_reg(0x4F);                       /* SET_RAM_Y_ADDRESS_COUNTER */
    epd_wr_data(ys & 0xFF);
    epd_wr_data((ys >> 8) & 0xFF);
}

static void epd_full_update(void)
{
    epd_wr_reg(0x22);                       /* Display Update Control 2 */
    epd_wr_data(0xF7);                      /* full-refresh waveform */
    epd_wr_reg(0x20);                       /* Master Activation */
    epd_wait_busy();
}

/* -- Public API ------------------------------------------------------------- */

void epd_gpio_init(void)
{
    gpio_config_t out = {
        .pin_bit_mask = (1ULL << EPD_PIN_SCK) | (1ULL << EPD_PIN_MOSI) |
                        (1ULL << EPD_PIN_RES) | (1ULL << EPD_PIN_DC)   |
                        (1ULL << EPD_PIN_CS)  | (1ULL << EPD_PIN_PWR),
        .mode = GPIO_MODE_OUTPUT,
    };
    gpio_config(&out);

    gpio_config_t in = {
        .pin_bit_mask = (1ULL << EPD_PIN_BUSY),
        .mode = GPIO_MODE_INPUT,
    };
    gpio_config(&in);

    gpio_set_level(EPD_PIN_PWR, 1);         /* power the panel */
    gpio_set_level(EPD_PIN_CS, 1);
    vTaskDelay(pdMS_TO_TICKS(100));
    ESP_LOGI(TAG, "GPIO ready (SCK%d MOSI%d RES%d DC%d CS%d BUSY%d PWR%d)",
             EPD_PIN_SCK, EPD_PIN_MOSI, EPD_PIN_RES, EPD_PIN_DC,
             EPD_PIN_CS, EPD_PIN_BUSY, EPD_PIN_PWR);
}

void epd_init(void)
{
    epd_reset();
    epd_wait_busy();
    epd_wr_reg(0x12);                       /* soft reset */
    epd_wait_busy();

    epd_wr_reg(0x21);                       /* Display Update Control 1 */
    epd_wr_data(0x40);
    epd_wr_data(0x00);

    epd_wr_reg(0x3C);                       /* Border Waveform */
    epd_wr_data(0x05);

    epd_wr_reg(0x11);                       /* Data Entry Mode */
    epd_wr_data(0x03);                      /* X+, Y+ */

    epd_set_window(0, 0, EPD_W - 1, EPD_H - 1);
    epd_set_cursor(0, 0);
    epd_wait_busy();
    ESP_LOGI(TAG, "init done");
}

void epd_clear(void)
{
    epd_set_window(0, 0, EPD_W - 1, EPD_H - 1);
    epd_set_cursor(0, 0);

    epd_wr_reg(0x24);                       /* write B/W RAM */
    for (int i = 0; i < EPD_BUF_SIZE; i++) {
        epd_wr_data(0xFF);
    }
    epd_wr_reg(0x26);                       /* write the second RAM bank (kept white) */
    for (int i = 0; i < EPD_BUF_SIZE; i++) {
        epd_wr_data(0xFF);
    }
    epd_full_update();
    ESP_LOGI(TAG, "clear (white) done");
}

void epd_display(const uint8_t *image)
{
    epd_set_window(0, 0, EPD_W - 1, EPD_H - 1);
    epd_set_cursor(0, 0);

    epd_wr_reg(0x24);                       /* write B/W RAM */
    for (int i = 0; i < EPD_BUF_SIZE; i++) {
        epd_wr_data(image[i]);
    }
    epd_full_update();
    ESP_LOGI(TAG, "display (full refresh) done");
}

void epd_display_part(uint16_t x, uint16_t y, uint16_t w, uint16_t h, const uint8_t *image)
{
    uint16_t wbytes = (w % 8 == 0) ? (w / 8) : (w / 8 + 1);

    epd_wr_reg(0x3C);                       /* partial border waveform */
    epd_wr_data(0x80);
    epd_wr_reg(0x21);                       /* Display Update Control 1 */
    epd_wr_data(0x00);
    epd_wr_data(0x00);
    epd_wr_reg(0x3C);
    epd_wr_data(0x80);

    epd_set_window(x, y, x + w - 1, y + h - 1);
    epd_set_cursor(x, y);

    epd_wr_reg(0x24);                       /* write B/W RAM (region) */
    for (int j = 0; j < h; j++) {
        for (int i = 0; i < wbytes; i++) {
            epd_wr_data(image[i + j * wbytes]);
        }
    }

    epd_wr_reg(0x22);                       /* partial-refresh waveform */
    epd_wr_data(0xFF);
    epd_wr_reg(0x20);                       /* Master Activation */
    epd_wait_busy();
    ESP_LOGI(TAG, "partial refresh (%u,%u %ux%u) done", x, y, w, h);
}

/* -- 4-gray ("mono CGA") ----------------------------------------------------
 * Grayscale waveform LUT + voltage settings for the SSD1683 4.2" panel. Taken
 * from the controller's reference driver (the official vendor driver — used only
 * to learn the LUT/sequence, not copied wholesale). Bytes 0..226 load via 0x32;
 * the tail bytes set 0x3F / 0x03 (gate) / 0x04 (source VSH1/VSH2/VSL) / 0x2C (VCOM). */
static const uint8_t EPD_LUT_4GRAY[233] = {
    0x01, 0x0A, 0x1B, 0x0F, 0x03, 0x01, 0x01,
    0x05, 0x0A, 0x01, 0x0A, 0x01, 0x01, 0x01,
    0x05, 0x08, 0x03, 0x02, 0x04, 0x01, 0x01,
    0x01, 0x04, 0x04, 0x02, 0x00, 0x01, 0x01,
    0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01,
    0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01,
    0x01, 0x0A, 0x1B, 0x0F, 0x03, 0x01, 0x01,
    0x05, 0x4A, 0x01, 0x8A, 0x01, 0x01, 0x01,
    0x05, 0x48, 0x03, 0x82, 0x84, 0x01, 0x01,
    0x01, 0x84, 0x84, 0x82, 0x00, 0x01, 0x01,
    0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01,
    0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01,
    0x01, 0x0A, 0x1B, 0x8F, 0x03, 0x01, 0x01,
    0x05, 0x4A, 0x01, 0x8A, 0x01, 0x01, 0x01,
    0x05, 0x48, 0x83, 0x82, 0x04, 0x01, 0x01,
    0x01, 0x04, 0x04, 0x02, 0x00, 0x01, 0x01,
    0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01,
    0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01,
    0x01, 0x8A, 0x1B, 0x8F, 0x03, 0x01, 0x01,
    0x05, 0x4A, 0x01, 0x8A, 0x01, 0x01, 0x01,
    0x05, 0x48, 0x83, 0x02, 0x04, 0x01, 0x01,
    0x01, 0x04, 0x04, 0x02, 0x00, 0x01, 0x01,
    0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01,
    0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01,
    0x01, 0x8A, 0x9B, 0x8F, 0x03, 0x01, 0x01,
    0x05, 0x4A, 0x01, 0x8A, 0x01, 0x01, 0x01,
    0x05, 0x48, 0x03, 0x42, 0x04, 0x01, 0x01,
    0x01, 0x04, 0x04, 0x42, 0x00, 0x01, 0x01,
    0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01,
    0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x02, 0x00, 0x00, 0x07, 0x17, 0x41, 0xA8,
    0x32, 0x30,
};

void epd_init_4gray(void)
{
    epd_reset();
    epd_wait_busy();
    epd_wr_reg(0x12);                       /* soft reset */
    epd_wait_busy();

    epd_wr_reg(0x21);                       /* Display Update Control 1 */
    epd_wr_data(0x00);
    epd_wr_data(0x00);

    epd_wr_reg(0x3C);                       /* Border Waveform */
    epd_wr_data(0x03);

    epd_wr_reg(0x0C);                       /* Booster soft-start */
    epd_wr_data(0x8B);
    epd_wr_data(0x9C);
    epd_wr_data(0xA4);
    epd_wr_data(0x0F);

    epd_wr_reg(0x32);                       /* load the grayscale LUT (227 bytes) */
    for (int i = 0; i < 227; i++) {
        epd_wr_data(EPD_LUT_4GRAY[i]);
    }
    epd_wr_reg(0x3F);                       /* LUT end option */
    epd_wr_data(EPD_LUT_4GRAY[227]);
    epd_wr_reg(0x03);                       /* gate voltage */
    epd_wr_data(EPD_LUT_4GRAY[228]);
    epd_wr_reg(0x04);                       /* source voltage (VSH1/VSH2/VSL) */
    epd_wr_data(EPD_LUT_4GRAY[229]);
    epd_wr_data(EPD_LUT_4GRAY[230]);
    epd_wr_data(EPD_LUT_4GRAY[231]);
    epd_wr_reg(0x2C);                       /* VCOM */
    epd_wr_data(EPD_LUT_4GRAY[232]);

    epd_wr_reg(0x11);                       /* data entry mode */
    epd_wr_data(0x03);
    epd_set_window(0, 0, EPD_W - 1, EPD_H - 1);
    epd_set_cursor(0, 0);
    epd_wait_busy();
    ESP_LOGI(TAG, "4-gray init done");
}

/* Pack the 2-bit-per-pixel image into one of the two RAM planes and stream it.
 * plane24 selects which mapping: each pixel's 2-bit code -> 1 bit per plane.
 *   0x24 plane: white & dark-grey -> 1, black & light-grey -> 0
 *   0x26 plane: white & light-grey -> 1, black & dark-grey -> 0 */
static void epd_send_gray_plane(const uint8_t *image, bool plane24)
{
    for (int m = 0; m < EPD_H; m++) {
        for (int i = 0; i < EPD_ROW_BYTES; i++) {
            uint8_t out = 0;
            for (int jj = 0; jj < 2; jj++) {
                uint8_t t1 = image[(m * EPD_ROW_BYTES + i) * 2 + jj];
                for (int kk = 0; kk < 2; kk++) {
                    for (int pix = 0; pix < 2; pix++) {
                        uint8_t code = t1 & 0xC0;
                        uint8_t bit;
                        if (plane24) {
                            bit = (code == 0xC0 || code == 0x40) ? 1 : 0;
                        } else {
                            bit = (code == 0xC0 || code == 0x80) ? 1 : 0;
                        }
                        out |= bit;
                        if (!(jj == 1 && kk == 1 && pix == 1)) {
                            out <<= 1;
                        }
                        t1 <<= 2;
                    }
                }
            }
            epd_wr_data(out);
        }
    }
}

void epd_display_4gray(const uint8_t *image)
{
    epd_set_window(0, 0, EPD_W - 1, EPD_H - 1);
    epd_set_cursor(0, 0);

    epd_wr_reg(0x24);
    epd_send_gray_plane(image, true);
    epd_wr_reg(0x26);
    epd_send_gray_plane(image, false);

    epd_wr_reg(0x22);                       /* 4-gray update (uses the loaded LUT) */
    epd_wr_data(0xCF);
    epd_wr_reg(0x20);
    epd_wait_busy();
    ESP_LOGI(TAG, "4-gray display done");
}

void epd_sleep(void)
{
    epd_wr_reg(0x10);                       /* deep sleep mode */
    epd_wr_data(0x01);
    vTaskDelay(pdMS_TO_TICKS(50));
    ESP_LOGI(TAG, "deep sleep");
}
