cmake_minimum_required(VERSION 3.16)

include($ENV{IDF_PATH}/tools/cmake/project.cmake)

project(ESP_UDP)

# Firmware version — injected by CI from git tags (-DFIRMWARE_VERSION_MAJOR=x …).
# When not provided (local build), config.h fallbacks apply via #ifndef guards.
if(DEFINED FIRMWARE_VERSION_MAJOR)
    add_compile_definitions(
        FIRMWARE_VERSION_MAJOR=${FIRMWARE_VERSION_MAJOR}
        FIRMWARE_VERSION_MINOR=${FIRMWARE_VERSION_MINOR}
        FIRMWARE_VERSION_PATCH=${FIRMWARE_VERSION_PATCH}
        FIRMWARE_BUILD_NUMBER=${FIRMWARE_BUILD_NUMBER}
    )
endif()

# LCD display type — passed by CI:
#   -DLCD_ILI9486=1  → ILI9486 480×320 RPi-compatible (OWLOSAirQuality custom board)
#   -DLCD_480x320=1  → ST7796  480×320
#   (none)           → ILI9341 320×240 (default)
if(DEFINED LCD_ILI9486)
    add_compile_definitions(_ILI9486_LCD=1)
elseif(DEFINED LCD_480x320)
    add_compile_definitions(_480x320_LCD=1)
endif()
add_compile_options(
    -Wall
    -Wextra
    -Wshadow
    -Wformat
    -Wconversion
    -Wsign-conversion
    -Wpedantic
)

idf_build_set_property(MINIMAL_BUILD OFF)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)

set(COMMON_FLAGS "-Os -flto -ffunction-sections -fdata-sections")
set(CMAKE_C_FLAGS   "${CMAKE_C_FLAGS} ${COMMON_FLAGS}")
set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} ${COMMON_FLAGS}")
set(CMAKE_EXE_LINKER_FLAGS "${CMAKE_EXE_LINKER_FLAGS} -Wl,--gc-sections -flto")