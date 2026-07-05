/**
 * @file epd_wide.c
 * @brief WIDE 792x272 e-paper driver — two SSD1683 in master/slave cascade (one CS).
 *        Clean ESP-IDF bit-bang port of the Elecrow CrowPanel 5.79" command sequence.
 *        See epd_wide.h. The logical 792x272 server frame is remapped into the cascade's
 *        800x272 RAM (gap + Rotation-180 mirror) inside epd_display().
 */
#include "epd_wide.h"

#include "driver/gpio.h"
#include "esp_rom_sys.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include <string.h>

static const char *TAG = "epd_wide";

/* -- Pins (shared wiring, identical to the 4.2") ---------------------------- */
#define EPD_PIN_SCK   12
#define EPD_PIN_MOSI  11
#define EPD_PIN_RES   47
#define EPD_PIN_DC    46
#define EPD_PIN_CS    45
#define EPD_PIN_BUSY  48
#define EPD_PIN_PWR   7    /* power-enable: must be HIGH or the panel is unpowered */

/* -- Cascade RAM geometry (private; the public logical geometry is in the header) --
 * Each SSD1683 is 400 wide but only 396 visible -> an 8-column gap at the seam. The RAM
 * buffer is 800x272 (100 bytes/row); the visible image is the 792x272 logical frame. */
#define RAM_MEM_W       800
#define RAM_ROW_BYTES   (RAM_MEM_W / 8)             /* 100 */
#define RAM_GATE_BITS   272
#define RAM_HALF_SOURCE_BYTES (400 / 8)             /* 50 bytes/row per controller */
#define RAM_HALF_BYTES  (RAM_HALF_SOURCE_BYTES * RAM_GATE_BITS)  /* 13600 */
#define RAM_SIZE        (RAM_ROW_BYTES * RAM_GATE_BITS)          /* 27200 */

/* Conservative bit-bang half-clock; the panel is happy well under 1 MHz. */
#define EPD_SPI_DELAY_US 1

/* BUSY is active-HIGH. Bound the wait so a mis-wired BUSY fails loudly. */
#define EPD_BUSY_TIMEOUT_MS 10000

/* Cascade RAM scratch (27200 B) — static, off-stack. epd_display() fills it from the
 * logical frame, then streams it to the two controllers. */
static uint8_t s_ram[RAM_SIZE];

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
 * The two cascaded controllers are selected by opcode, not by CS: master uses the plain
 * registers, slave uses (master | 0x80) gated on by 0x91 = 0x04. Data-entry 0x05 =
 * Y-decrement / X-increment; the slave half is X-mirrored to align across the seam. */

static void epd_set_ram_master_window(void)
{
    epd_wr_reg(0x11);
    epd_wr_data(0x05);
    epd_wr_reg(0x44);
    epd_wr_data(0x00);
    epd_wr_data(0x31);               /* 0x31 = 49 -> (49+1)*8 = 400 px */
    epd_wr_reg(0x45);
    epd_wr_data(0x0F);
    epd_wr_data(0x01);               /* YStart = 271 */
    epd_wr_data(0x00);
    epd_wr_data(0x00);               /* YEnd = 0 */
}

static void epd_set_ram_master_cursor(void)
{
    epd_wr_reg(0x4E);
    epd_wr_data(0x00);
    epd_wr_reg(0x4F);
    epd_wr_data(0x0F);
    epd_wr_data(0x01);               /* Y = 271 */
}

static void epd_set_ram_slave_window(void)
{
    epd_wr_reg(0x91);                /* enable slave-controller addressing */
    epd_wr_data(0x04);
    epd_wr_reg(0xC4);
    epd_wr_data(0x31);
    epd_wr_data(0x00);
    epd_wr_reg(0xC5);
    epd_wr_data(0x0F);
    epd_wr_data(0x01);
    epd_wr_data(0x00);
    epd_wr_data(0x00);
}

static void epd_set_ram_slave_cursor(void)
{
    epd_wr_reg(0xCE);
    epd_wr_data(0x31);
    epd_wr_reg(0xCF);
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

/* Stream one half's 13600 bytes from s_ram. The cascade scans column-major (Y-decrement
 * inner), so read down each column (272 rows) before advancing X. start_col = 0 master,
 * 50 slave. */
static void epd_stream_half(int start_col)
{
    int templine = 0;
    int tempcol = start_col;
    for (int i = 0; i < RAM_HALF_BYTES; i++) {
        epd_wr_data(s_ram[templine * RAM_ROW_BYTES + tempcol]);
        templine++;
        if (templine >= RAM_GATE_BITS) {
            tempcol++;
            templine = 0;
        }
    }
}

static void epd_stream_const(uint8_t value)
{
    for (int i = 0; i < RAM_HALF_BYTES; i++) {
        epd_wr_data(value);
    }
}

/* Set one logical pixel (0..791, 0..271) into s_ram, applying the 8px gap skip and the
 * Rotation-180 mirror so it lands in the right cascade RAM cell. */
static void ram_set_pixel(int x, int y, int black)
{
    int xp = x;
    if (xp >= 396) {
        xp += 8;                            /* skip the 8-column inter-panel gap */
    }
    int mx = RAM_MEM_W - 1 - xp;            /* Rotation 180: mirror X into RAM */
    int my = EPD_H - 1 - y;                 /* Rotation 180: mirror Y */
    int addr = (mx >> 3) + my * RAM_ROW_BYTES;
    uint8_t mask = 0x80 >> (mx & 7);
    if (black) {
        s_ram[addr] &= ~mask;
    } else {
        s_ram[addr] |= mask;
    }
}

/* Build s_ram from a logical 792x272 frame, then stream both halves + full refresh. */
static void epd_push_logical(const uint8_t *image)
{
    memset(s_ram, 0xFF, sizeof(s_ram));     /* white */
    for (int y = 0; y < EPD_H; y++) {
        const uint8_t *row = image + y * EPD_ROW_BYTES;
        for (int x = 0; x < EPD_W; x++) {
            if (!(row[x >> 3] & (0x80 >> (x & 7)))) {   /* logical bit 0 = black */
                ram_set_pixel(x, y, 1);
            }
        }
    }

    epd_set_ram_master_window();
    epd_set_ram_master_cursor();
    epd_wr_reg(0x24);
    epd_stream_half(0);
    epd_set_ram_master_cursor();
    epd_wr_reg(0x26);
    epd_stream_const(0x00);

    epd_set_ram_slave_window();
    epd_set_ram_slave_cursor();
    epd_wr_reg(0xA4);
    epd_stream_half(RAM_HALF_SOURCE_BYTES);
    epd_set_ram_slave_cursor();
    epd_wr_reg(0xA6);
    epd_stream_const(0x00);

    epd_full_update();
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

void epd_display(const uint8_t *image)
{
    epd_push_logical(image);
    ESP_LOGI(TAG, "display (full refresh, both halves) done");
}

void epd_clear(void)
{
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

void epd_display_part(uint16_t x, uint16_t y, uint16_t w, uint16_t h, const uint8_t *image)
{
    /* v1: no true partial on the cascade. A whole-screen call is a full refresh; a
     * sub-region call (boot status strip) is a no-op until partial is ported (Step 4). */
    if (x == 0 && y == 0 && w == EPD_W && h == EPD_H) {
        epd_push_logical(image);
        ESP_LOGI(TAG, "display_part whole-screen -> full refresh");
    } else {
        ESP_LOGD(TAG, "display_part sub-region (%u,%u %ux%u) ignored (no partial yet)",
                 x, y, w, h);
    }
}

/* -- 4-gray (verbatim Waveshare EPD_5in79.c port, proven on hardware) --------
 * The reference for this exact dual-SSD1683 792x272 glass is Waveshare's driver
 * (waveshareteam/e-Paper). Verified on our panel by the diagnostics-wide probe
 * v4: both halves, full height, four clean tones. Key facts vs the mono path:
 *   - 0x91 is the SLAVE's data-entry register (mono pair 0x05/0x04, gray pair
 *     0x01/0x00 - row-major, slave X mirrored), not an addressing enable;
 *   - both windows/cursors are programmed ONCE in init; the four full-plane
 *     streams (0x24/0x26/0xA4/0xA6) follow with no cursor rewrites (wrap);
 *   - the image is the server's LINEAR 2bpp frame (4 px/byte MSB-first, stride
 *     198 B, code 3=white 2=light 1=dark 0=black); the hardware handles the
 *     mirror and the seam (4+4 hidden columns: master reads row bytes 0..99,
 *     slave 98..197);
 *   - the probe showed this addressing renders our logical frame rotated 180
 *     degrees vs the mono mapping, so packing reads the source flipped both
 *     ways - the on-glass orientation matches epd_display() exactly;
 *   - activation 0x22 = 0xCF (display with the register LUT, no OTP reload). */
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

void epd_init_4gray(void)
{
    epd_reset();
    epd_wait_busy();
    epd_wr_reg(0x12);                       /* soft reset (reaches both in cascade) */
    epd_wait_busy();

    epd_wr_reg(0x0C);                       /* booster soft-start */
    epd_wr_data(0x8B);
    epd_wr_data(0x9C);
    epd_wr_data(0xA6);
    epd_wr_data(0x0F);

    epd_wr_reg(0x3C);                       /* border waveform */
    epd_wr_data(0x81);

    epd_wait_busy();

    epd_wr_reg(0x11);                       /* master data entry: row-major, X inc */
    epd_wr_data(0x01);
    epd_wr_reg(0x44);
    epd_wr_data(0x00);
    epd_wr_data(0x31);
    epd_wr_reg(0x45);
    epd_wr_data(0x0F);
    epd_wr_data(0x01);
    epd_wr_data(0x00);
    epd_wr_data(0x00);
    epd_wr_reg(0x4E);
    epd_wr_data(0x00);
    epd_wr_reg(0x4F);
    epd_wr_data(0x0F);
    epd_wr_data(0x01);

    epd_wait_busy();

    epd_wr_reg(0x91);                       /* slave data entry (X mirrored) */
    epd_wr_data(0x00);
    epd_wr_reg(0xC4);
    epd_wr_data(0x31);
    epd_wr_data(0x00);
    epd_wr_reg(0xC5);
    epd_wr_data(0x0F);
    epd_wr_data(0x01);
    epd_wr_data(0x00);
    epd_wr_data(0x00);
    epd_wr_reg(0xCE);
    epd_wr_data(0x31);
    epd_wr_reg(0xCF);
    epd_wr_data(0x0F);
    epd_wr_data(0x01);

    epd_wr_reg(0x32);                       /* grayscale waveform LUT (227 bytes) */
    for (int i = 0; i < 227; i++) {
        epd_wr_data(EPD_LUT_4GRAY[i]);
    }
    epd_wr_reg(0x3F);                       /* LUT end option */
    epd_wr_data(EPD_LUT_4GRAY[227]);
    epd_wr_reg(0x03);                       /* gate voltage */
    epd_wr_data(EPD_LUT_4GRAY[228]);
    epd_wr_reg(0x04);                       /* source voltage VSH1/VSH2/VSL */
    epd_wr_data(EPD_LUT_4GRAY[229]);
    epd_wr_data(EPD_LUT_4GRAY[230]);
    epd_wr_data(EPD_LUT_4GRAY[231]);
    epd_wr_reg(0x2C);                       /* VCOM */
    epd_wr_data(EPD_LUT_4GRAY[232]);

    ESP_LOGI(TAG, "4-gray init done (cascade, Waveshare sequence)");
}

/* 2-bit code of the SOURCE pixel for the stream position (p, j), applying the
 * 180-degree flip that aligns the Waveshare addressing with our logical frame. */
static inline uint8_t g4_src_code(const uint8_t *image, int p, int j)
{
    int x = (EPD_W - 1) - p;
    int y = (EPD_H - 1) - j;
    int stride = EPD_ROW_BYTES * 2;                 /* 198 bytes per 2bpp row */
    uint8_t b = image[y * stride + (x >> 2)];
    return (uint8_t)((b >> (6 - 2 * (x & 3))) & 0x03);
}

/* Pack 8 stream pixels starting at their-pixel column p0 of row j into one plane
 * byte. plane24 bit = code&1 (white/dark 1), plane26 bit = code>>1 (white/light 1). */
static uint8_t g4_pack8(const uint8_t *image, int p0, int j, int plane24)
{
    uint8_t out = 0;
    for (int k = 0; k < 8; k++) {
        uint8_t code = g4_src_code(image, p0 + k, j);
        uint8_t bit = plane24 ? (code & 0x01) : ((code >> 1) & 0x01);
        out = (uint8_t)((out << 1) | bit);
    }
    return out;
}

void epd_display_4gray(const uint8_t *image)
{
    /* Four full-plane row-major streams, no cursor rewrites between them: master
     * reads their-pixel columns 0..399, slave 392..791 (seam overlap is hidden). */
    const int width  = (EPD_W % 16 == 0) ? EPD_W / 16 : EPD_W / 16 + 1;   /* 50 */

    epd_wr_reg(0x24);
    for (int j = 0; j < EPD_H; j++)
        for (int i = 0; i < width; i++)
            epd_wr_data(g4_pack8(image, i * 8, j, 1));

    epd_wr_reg(0x26);
    for (int j = 0; j < EPD_H; j++)
        for (int i = 0; i < width; i++)
            epd_wr_data(g4_pack8(image, i * 8, j, 0));

    epd_wr_reg(0xA4);
    for (int j = 0; j < EPD_H; j++)
        for (int i = 0; i < width; i++)
            epd_wr_data(g4_pack8(image, (i + width - 1) * 8, j, 1));

    epd_wr_reg(0xA6);
    for (int j = 0; j < EPD_H; j++)
        for (int i = 0; i < width; i++)
            epd_wr_data(g4_pack8(image, (i + width - 1) * 8, j, 0));

    epd_wr_reg(0x22);                       /* display with the register LUT */
    epd_wr_data(0xCF);
    epd_wr_reg(0x20);
    vTaskDelay(pdMS_TO_TICKS(100));         /* reference: settle before busy-wait */
    epd_wait_busy();
    ESP_LOGI(TAG, "4-gray display (both halves) done");
}

void epd_display_part_4gray(uint16_t x, uint16_t y, uint16_t w, uint16_t h, const uint8_t *image)
{
    /* No true 4-gray partial on the cascade: whole-screen falls back to a full
     * 4-gray refresh, sub-region calls are ignored (mirrors epd_display_part). */
    if (x == 0 && y == 0 && w == EPD_W && h == EPD_H) {
        epd_display_4gray(image);
    } else {
        ESP_LOGD(TAG, "4-gray part sub-region (%u,%u %ux%u) ignored", x, y, w, h);
    }
}

void epd_sleep(void)
{
    epd_wr_reg(0x10);                       /* deep sleep mode */
    epd_wr_data(0x01);
    vTaskDelay(pdMS_TO_TICKS(50));
    ESP_LOGI(TAG, "deep sleep");
}
