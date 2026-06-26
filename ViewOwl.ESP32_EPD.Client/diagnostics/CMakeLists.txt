# ViewOwl 4.2" e-paper (400x300, SSD1680) bring-up diagnostics.
# Standalone ESP-IDF project — proves the wiring + the clean EPD driver before the
# driver is folded into the full ViewOwl.ESP32_EPD.Client. Target is esp32s3 (the
# board uses GPIO 45-48, which only the S3 exposes).
cmake_minimum_required(VERSION 3.16)
include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(epd42_diag)
