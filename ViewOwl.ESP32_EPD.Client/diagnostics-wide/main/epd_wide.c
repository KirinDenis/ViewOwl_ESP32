/**
 * @file epd_wide.c
 * @brief WIDE 792x272 e-paper driver — two SSD1683 in master/slave cascade (one CS).
 *        Clean ESP-IDF bit-bang port of the Elecrow CrowPanel 5.79" command sequence.
 */
#include "epd_wide.h"

#include "driver/gpio.h"
#include "esp_rom_sys.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

static const char *TAG = "epd_wide";

/* Conservative bit-bang half-clock; the panel is happy well under 1 MHz. */
#define EPD_SPI_DELAY_US 1

/* BUSY is active-HIGH. Bound the wait so a mis-wired BUSY fails loudly. */
#define EPD_BUSY_TIMEOUT_MS 10000

/* -- Low-level SPI bit-bang (single shared CS) ----------------------------- */

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
    vTaskDelay(pdMS_TO_TICKS(10));
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

/* -- Master / slave RAM addressing -----------------------------------------
 * The two cascaded SSD1683 controllers are selected by opcode, not by CS:
 * master uses the plain registers, slave uses (master | 0x80) and is gated on by
 * 0x91 = 0x04. Data-entry mode 0x05 = Y-decrement / X-increment; the slave half is
 * X-mirrored (XStart = 0x31 -> XEnd = 0x00) to align with the master across the seam. */

static void epd_set_ram_master_window(void)
{
    epd_wr_reg(0x11);                /* data entry: Y decrement, X increment */
    epd_wr_data(0x05);
    epd_wr_reg(0x44);                /* RAM X start/end (byte units) */
    epd_wr_data(0x00);
    epd_wr_data(0x31);               /* 0x31 = 49 -> (49+1)*8 = 400 px */
    epd_wr_reg(0x45);                /* RAM Y start/end (16-bit) */
    epd_wr_data(0x0F);
    epd_wr_data(0x01);               /* YStart = 0x10F = 271 */
    epd_wr_data(0x00);
    epd_wr_data(0x00);               /* YEnd = 0 */
}

static void epd_set_ram_master_cursor(void)
{
    epd_wr_reg(0x4E);                /* RAM X counter */
    epd_wr_data(0x00);
    epd_wr_reg(0x4F);                /* RAM Y counter */
    epd_wr_data(0x0F);
    epd_wr_data(0x01);               /* Y = 271 */
}

static void epd_set_ram_slave_window(void)
{
    epd_wr_reg(0x91);                /* enable slave-controller addressing */
    epd_wr_data(0x04);
    epd_wr_reg(0xC4);                /* slave RAM X start/end (mirrored) */
    epd_wr_data(0x31);
    epd_wr_data(0x00);
    epd_wr_reg(0xC5);                /* slave RAM Y start/end */
    epd_wr_data(0x0F);
    epd_wr_data(0x01);
    epd_wr_data(0x00);
    epd_wr_data(0x00);
}

static void epd_set_ram_slave_cursor(void)
{
    epd_wr_reg(0xCE);                /* slave RAM X counter */
    epd_wr_data(0x31);
    epd_wr_reg(0xCF);                /* slave RAM Y counter */
    epd_wr_data(0x0F);
    epd_wr_data(0x01);
}

static void epd_full_update(void)
{
    epd_wr_reg(0x22);                /* Display Update Control 2 */
    epd_wr_data(0xF7);               /* full-refresh waveform (loads temp + OTP LUT) */
    epd_wr_reg(0x20);                /* Master Activation — refreshes BOTH halves */
    epd_wait_busy();
}

/* Stream one half's 13600 bytes. The cascade scans column-major (Y-decrement inner),
 * so the buffer is read down each column (272 rows) before advancing X. start_col is
 * the byte column where this half begins: 0 for master, 50 for slave. */
static void epd_stream_half(const uint8_t *image, int start_col)
{
    int templine = 0;
    int tempcol = start_col;
    for (int i = 0; i < EPD_HALF_BYTES; i++) {
        uint8_t b = image[templine * EPD_ROW_BYTES + tempcol];
        templine++;
        if (templine >= EPD_GATE_BITS) {
            tempcol++;
            templine = 0;
        }
        epd_wr_data(b);
    }
}

static void epd_stream_const(uint8_t value)
{
    for (int i = 0; i < EPD_HALF_BYTES; i++) {
        epd_wr_data(value);
    }
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
    gpio_set_level(EPD_PIN_RES, 1);
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
    ESP_LOGI(TAG, "cascade init done");
}

void epd_set_pixel(uint8_t *buf, int x, int y, int black)
{
    if (x < 0 || x >= EPD_LOGICAL_W || y < 0 || y >= EPD_LOGICAL_H) {
        return;
    }
    int xp = x;
    if (xp >= 396) {
        xp += 8;                            /* skip the 8-column inter-panel gap */
    }
    int mx = EPD_MEM_W - 1 - xp;            /* Rotation 180: mirror X into RAM */
    int my = EPD_LOGICAL_H - 1 - y;         /* Rotation 180: mirror Y */
    int addr = (mx >> 3) + my * EPD_ROW_BYTES;
    uint8_t mask = 0x80 >> (mx & 7);        /* MSB = leftmost RAM pixel */
    if (black) {
        buf[addr] &= ~mask;
    } else {
        buf[addr] |= mask;
    }
}

void epd_display(const uint8_t *image)
{
    /* Master half: B/W RAM (0x24) = image, "previous" RAM (0x26) = black for a clean
     * full transition. */
    epd_set_ram_master_window();
    epd_set_ram_master_cursor();
    epd_wr_reg(0x24);
    epd_stream_half(image, 0);
    epd_set_ram_master_cursor();
    epd_wr_reg(0x26);
    epd_stream_const(0x00);

    /* Slave half: same, via the bit7-set opcodes (0xA4 / 0xA6). */
    epd_set_ram_slave_window();
    epd_set_ram_slave_cursor();
    epd_wr_reg(0xA4);
    epd_stream_half(image, EPD_HALF_SOURCE_BYTES);
    epd_set_ram_slave_cursor();
    epd_wr_reg(0xA6);
    epd_stream_const(0x00);

    epd_full_update();
    ESP_LOGI(TAG, "display (full refresh, both halves) done");
}

void epd_clear(void)
{
    /* White new RAM, black "previous" RAM, both halves -> clean white full refresh. */
    epd_set_ram_master_window();
    epd_set_ram_master_cursor();
    epd_wr_reg(0x24);
    epd_stream_const(0xFF);
    epd_set_ram_master_cursor();
    epd_wr_reg(0x26);
    epd_stream_const(0x00);

    epd_set_ram_slave_window();
    epd_set_ram_slave_cursor();
    epd_wr_reg(0xA4);
    epd_stream_const(0xFF);
    epd_set_ram_slave_cursor();
    epd_wr_reg(0xA6);
    epd_stream_const(0x00);

    epd_full_update();
    ESP_LOGI(TAG, "clear (white) done");
}

/* -- 4-gray, Waveshare-exact port (EPD_5in79.c) ------------------------------
 * Probes v1-v3 used the 4.2" LUT with our own geometry: tones appeared on the
 * master but scan geometry broke and the slave was mud. Waveshare ships a
 * PROVEN 4-gray driver for this same dual-SSD1683 792x272 glass — everything
 * below is a verbatim port of it. LUT layout: bytes 0..226 load via 0x32; tail =
 * EOPT(0x3F) VGH(0x03) VSH1/VSH2/VSL(0x04) VCOM(0x2C). */
static const uint8_t EPD_LUT_4GRAY[233] = {
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x01, 0x4A, 0x00, 0x00, 0x00, 0x01, 0x00,
    0x01, 0x82, 0x42, 0x00, 0x00, 0x10, 0x00,
    0x01, 0x8A, 0x00, 0x00, 0x00, 0x01, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x01, 0x41, 0x00, 0x00, 0x00, 0x01, 0x00,
    0x01, 0x82, 0x42, 0x00, 0x00, 0x10, 0x00,
    0x01, 0x81, 0x00, 0x00, 0x00, 0x01, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x01, 0x81, 0x00, 0x00, 0x00, 0x01, 0x00,
    0x01, 0x82, 0x42, 0x00, 0x00, 0x10, 0x00,
    0x01, 0x41, 0x00, 0x00, 0x00, 0x01, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x01, 0x8A, 0x00, 0x00, 0x00, 0x01, 0x00,
    0x01, 0x82, 0x42, 0x00, 0x00, 0x10, 0x00,
    0x01, 0x4A, 0x00, 0x00, 0x00, 0x01, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x02, 0x00, 0x00,                       /* FR, XON */
    0x22, 0x17, 0x41, 0xA8, 0x32, 0x40,     /* EOPT VGH VSH1 VSH2 VSL VCOM */
};

/* Load the grayscale LUT + tail voltages (Waveshare EPD_5in79_Lut, verbatim). */
static void epd_load_gray_lut(void)
{
    epd_wr_reg(0x32);                       /* grayscale waveform LUT (227 bytes) */
    for (int i = 0; i < 227; i++) {
        epd_wr_data(EPD_LUT_4GRAY[i]);
    }
    epd_wr_reg(0x3F);                       /* LUT end option */
    epd_wr_data(EPD_LUT_4GRAY[227]);
    epd_wr_reg(0x03);                       /* gate voltage (VGH) */
    epd_wr_data(EPD_LUT_4GRAY[228]);
    epd_wr_reg(0x04);                       /* source voltage (VSH1/VSH2/VSL) */
    epd_wr_data(EPD_LUT_4GRAY[229]);
    epd_wr_data(EPD_LUT_4GRAY[230]);
    epd_wr_data(EPD_LUT_4GRAY[231]);
    epd_wr_reg(0x2C);                       /* VCOM */
    epd_wr_data(EPD_LUT_4GRAY[232]);
}

void epd_init_4gray_ws(void)
{
    /* Waveshare EPD_5in79_Init_4Gray, command for command. Notable vs our v1-v3
     * attempts: booster tail 0xA6 (not 0xA4), border 0x81 (not 0x03), data entry
     * 0x11=0x01 (ROW-major) with the slave's mirrored counterpart 0x91=0x00, both
     * windows/cursors programmed once here, and NO 0x01/MUX or 0x21 writes. */
    epd_reset();
    epd_wait_busy();
    epd_wr_reg(0x12);                       /* soft reset (reaches both in cascade) */
    epd_wait_busy();

    epd_wr_reg(0x0C);                       /* Booster soft-start */
    epd_wr_data(0x8B);
    epd_wr_data(0x9C);
    epd_wr_data(0xA6);
    epd_wr_data(0x0F);

    epd_wr_reg(0x3C);                       /* Border Waveform */
    epd_wr_data(0x81);

    epd_wait_busy();

    epd_wr_reg(0x11);                       /* data entry: row-major, X increment */
    epd_wr_data(0x01);

    epd_wr_reg(0x44);                       /* master RAM X start/end */
    epd_wr_data(0x00);
    epd_wr_data(0x31);
    epd_wr_reg(0x45);                       /* master RAM Y start/end */
    epd_wr_data(0x0F);
    epd_wr_data(0x01);
    epd_wr_data(0x00);
    epd_wr_data(0x00);
    epd_wr_reg(0x4E);                       /* master RAM X counter */
    epd_wr_data(0x00);
    epd_wr_reg(0x4F);                       /* master RAM Y counter */
    epd_wr_data(0x0F);
    epd_wr_data(0x01);

    epd_wait_busy();

    epd_wr_reg(0x91);                       /* slave data entry (mirrored X) */
    epd_wr_data(0x00);

    epd_wr_reg(0xC4);                       /* slave RAM X start/end (mirrored) */
    epd_wr_data(0x31);
    epd_wr_data(0x00);
    epd_wr_reg(0xC5);                       /* slave RAM Y start/end */
    epd_wr_data(0x0F);
    epd_wr_data(0x01);
    epd_wr_data(0x00);
    epd_wr_data(0x00);
    epd_wr_reg(0xCE);                       /* slave RAM X counter */
    epd_wr_data(0x31);
    epd_wr_reg(0xCF);                       /* slave RAM Y counter */
    epd_wr_data(0x0F);
    epd_wr_data(0x01);

    epd_load_gray_lut();

    ESP_LOGI(TAG, "4-gray init done (Waveshare-exact)");
}

void epd_g2_set_pixel(uint8_t *img, int x, int y, uint8_t code)
{
    if (x < 0 || x >= EPD_LOGICAL_W || y < 0 || y >= EPD_LOGICAL_H) {
        return;
    }
    /* Linear logical image, 4 px/byte MSB-first, byte index matches the
     * Waveshare (j*Width1 + i)*2 + o addressing: row stride 198 bytes. */
    int idx = y * EPD_G2_STRIDE + (x >> 2);
    int shift = 6 - 2 * (x & 3);
    img[idx] = (uint8_t)((img[idx] & ~(0x03 << shift)) | ((code & 0x03) << shift));
}

/* Pack 8 logical pixels (2 image bytes at idx) into one plane byte.
 * plane24: white(3)/gray2(1) -> 1; plane26: white(3)/gray1(2) -> 1. Verbatim
 * Waveshare mapping (bit = code&1 for 0x24, bit = code>>1 for 0x26). */
static uint8_t epd_g2_pack8(const uint8_t *img, int idx, int plane24)
{
    uint8_t out = 0;
    for (int o = 0; o < 2; o++) {
        uint8_t t = img[idx + o];
        for (int k = 0; k < 4; k++) {
            uint8_t code = (uint8_t)((t >> 6) & 0x03);
            uint8_t bit = plane24 ? (code & 0x01) : ((code >> 1) & 0x01);
            out = (uint8_t)((out << 1) | bit);
            t <<= 2;
        }
    }
    return out;
}

void epd_display_4gray_ws(const uint8_t *img)
{
    /* Waveshare EPD_5in79_4GrayDisplay, verbatim: four full-plane row-major
     * streams, NO cursor writes in between (the windows wrap), master reads row
     * bytes 0..99, slave reads bytes 98..197 (the seam hides 4+4 columns). */
    const int width  = (EPD_LOGICAL_W % 16 == 0) ? EPD_LOGICAL_W / 16
                                                 : EPD_LOGICAL_W / 16 + 1;  /* 50 */
    const int height = EPD_LOGICAL_H;

    epd_wr_reg(0x24);
    for (int j = 0; j < height; j++)
        for (int i = 0; i < width; i++)
            epd_wr_data(epd_g2_pack8(img, j * EPD_G2_STRIDE + i * 2, 1));

    epd_wr_reg(0x26);
    for (int j = 0; j < height; j++)
        for (int i = 0; i < width; i++)
            epd_wr_data(epd_g2_pack8(img, j * EPD_G2_STRIDE + i * 2, 0));

    epd_wr_reg(0xA4);
    for (int j = 0; j < height; j++)
        for (int i = 0; i < width; i++)
            epd_wr_data(epd_g2_pack8(img, j * EPD_G2_STRIDE + (i + width - 1) * 2, 1));

    epd_wr_reg(0xA6);
    for (int j = 0; j < height; j++)
        for (int i = 0; i < width; i++)
            epd_wr_data(epd_g2_pack8(img, j * EPD_G2_STRIDE + (i + width - 1) * 2, 0));

    epd_wr_reg(0x22);                       /* display with the loaded gray LUT */
    epd_wr_data(0xCF);
    epd_wr_reg(0x20);                       /* Master Activation — both halves */
    vTaskDelay(pdMS_TO_TICKS(100));         /* Waveshare: delay before busy-wait */
    epd_wait_busy();
    ESP_LOGI(TAG, "4-gray display (Waveshare-exact) done");
}

void epd_sleep(void)
{
    epd_wr_reg(0x10);                       /* deep sleep mode */
    epd_wr_data(0x01);
    vTaskDelay(pdMS_TO_TICKS(50));
    ESP_LOGI(TAG, "deep sleep");
}
