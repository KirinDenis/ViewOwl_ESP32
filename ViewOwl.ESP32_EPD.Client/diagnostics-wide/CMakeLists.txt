# ViewOwl WIDE dual-half e-paper bring-up diagnostics.
# Standalone ESP-IDF project — the "polygon" for discovering how the WIDE panel's
# SECOND half is driven (the proven 4.2" code lights only ONE half). Same SSD1683
# family, same SCK/MOSI/BUSY/PWR wiring; the unknown is the 2nd controller's select.
# Target is esp32s3 (the board wires the EPD control lines to GPIO 45-48, S3-only).
cmake_minimum_required(VERSION 3.16)
# Trim the build to main + its transitive requirements only. The polygon is pure
# GPIO bit-bang — without this, the default component set drags in esp_lcd and
# friends, which we neither use nor want to compile (a toolchain ICE inside
# esp_lcd_panel_rgb.c once broke this build without touching our code).
set(COMPONENTS main)
include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(epd_wide_diag)
