/**
 * @file epd_panel.h
 * @brief Build-flag selector for the e-paper driver.
 *
 * The EPD client builds for one of two panels, chosen by the EPD_792x272 build flag
 * (passed by CI / idf.py as -DEPD_792x272=1). Both drivers expose the SAME API
 * (EPD_W/EPD_H/EPD_ROW_BYTES/EPD_BUF_SIZE + epd_*), so all consumers include THIS
 * header and stay panel-agnostic:
 *   - default        -> 4.2" 400x300 single SSD1683        (epd_4in2.h)
 *   - EPD_792x272    -> 5.79" 792x272 SSD1683 cascade       (epd_wide.h)
 */
#pragma once

#if defined(EPD_792x272)
#include "epd_wide.h"
#else
#include "epd_4in2.h"
#endif
