cmake_minimum_required(VERSION 3.16)

# ViewOwl ESP32-C3 client — separate firmware for the round GC9A01 displays
# (Elecrow CrowPanel 1.28"). Target is fixed to esp32c3 via sdkconfig.defaults.
# SoC-agnostic logic (UDP protocol, frame decode, WiFi, NVS) is shared from
# ../ViewOwl.ESP32.Client/main where possible; only the LCD layer + board config
# are C3-specific. The wire-protocol contract (packet.h) must stay identical.
include($ENV{IDF_PATH}/tools/cmake/project.cmake)

project(viewowl-c3-client)
