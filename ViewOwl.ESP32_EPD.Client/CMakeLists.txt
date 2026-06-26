# ViewOwl ESP32-S3 e-paper client — full firmware (the diagnostics/ subproject was
# the bring-up). Target is esp32s3 (4.2" SSD1683 e-paper, GPIO 45-48 power/SPI).
cmake_minimum_required(VERSION 3.16)
include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(viewowl-epd-client)
