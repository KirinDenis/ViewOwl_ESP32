#pragma once

#include "esp_lcd_panel_vendor.h"
#include "driver/spi_master.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * @brief Create an LCD panel handle for the ILI9486 controller.
 *
 * Targets RPi-compatible 3.5" 480×320 SPI panels (Waveshare RPi LCD A V3):
 *   - 74HC245 level-shifter buffers require 16-bit SPI transactions per byte
 *   - 16-bit colour (COLMOD 0x55)
 *   - No display-inversion (INVOFF) — use INVON for the (B) variant
 *   - BGR pixel order
 *   - 20 MHz SPI maximum
 *
 * Uses raw SPI transactions (16-bit per byte, manual CS) instead of the
 * standard esp_lcd panel IO layer.  esp_lcd with lcd_cmd_bits=8 does NOT
 * work — the buffers do not latch and the panel stays gray.
 *
 * @param spi         SPI device handle created by spi_bus_add_device().
 *                    Must be configured with spics_io_num = -1 (manual CS).
 * @param dc_gpio_num GPIO number for the D/C (data/command) line.
 * @param cs_gpio_num GPIO number for chip select (manual toggle).
 * @param rst_gpio_num GPIO number for hardware reset, or -1 if not wired.
 * @param bgr_order   true → set MADCTL BGR bit (Waveshare (A) variant).
 * @param ret_panel   Output: LCD panel handle.
 * @return ESP_OK on success, error code otherwise.
 */
esp_err_t esp_lcd_new_panel_ili9486(spi_device_handle_t spi,
                                     int dc_gpio_num,
                                     int cs_gpio_num,
                                     int rst_gpio_num,
                                     bool bgr_order,
                                     esp_lcd_panel_handle_t *ret_panel);

/**
 * @brief Blit a rectangle of pixel data to the ILI9486 display.
 *
 * Sets the address window (CASET/RASET) and sends the pixel data via DMA.
 * Called from lcd_render.c and lcd_text.c in place of the io_handle path.
 *
 * Pixels must be packed in memory order: {hi_byte, lo_byte} per pixel
 * (big-endian per pixel, as produced by the server's BGR565 encoder).
 * DMA sends bytes in memory order — no byte reversal.
 *
 * @param x1     Left column (inclusive).
 * @param y1     Top row (inclusive).
 * @param x2     Right column (exclusive).
 * @param y2     Bottom row (exclusive).
 * @param pixels Pixel data buffer (DMA-accessible SRAM, not flash).
 * @param len    Byte count of pixel data.
 */
void ili9486_blit(int x1, int y1, int x2, int y2,
                  const uint8_t *pixels, size_t len);

#ifdef __cplusplus
}
#endif
