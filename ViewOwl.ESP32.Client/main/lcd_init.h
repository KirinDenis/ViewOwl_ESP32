#pragma once

#include "esp_lcd_panel_io.h"

/**
 * @brief Initialises the SPI bus, LCD panel IO, and the display controller.
 *
 * Must be called once before any lcd_* or render function.
 */
void lcd_init(void);

/**
 * @brief LCD panel IO handle used by all render functions.
 *
 * Assigned inside lcd_init().  Declared here so lcd_render.c and any other
 * module that drives the display can include a single header instead of
 * repeating an extern declaration in every translation unit.
 */
extern esp_lcd_panel_io_handle_t io_handle;