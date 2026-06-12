idf_component_register(SRCS "udp_protocol.c" "lcd_render.c" "frame_buffer.c" "lcd_log.c" "lcd_text.c"
                            "lcd_tui.c"
                            "main.c" "lcd_init.c" "wifi_init.c" "udp_client.c" "frame_decoder.c"
                            "lz4_block.c" "nvs_storage.c" "serial_provision.c" "flash_frames.c"
                            "esp_lcd_ili9486.c"
                       INCLUDE_DIRS "."
                       REQUIRES espressif__esp_lcd_st7796 espressif__esp_lcd_ili9341 esp_partition
                       PRIV_REQUIRES esp_timer esp_wifi esp_event nvs_flash lwip driver spi_flash esp_system)

# Convert the cmake cache variable into the C preprocessor define that
# config.h and lcd_init.c branch on.
#   -DLCD_ILI9486=1  → ILI9486 480×320 RPi-compatible (OWLOSAirQuality board)
#   -DLCD_480x320=1  → ST7796  480×320
#   (none)           → ILI9341 320×240 (default)
if(DEFINED LCD_ILI9486)
    target_compile_definitions(${COMPONENT_LIB} PUBLIC _ILI9486_LCD=1)
    message(STATUS "Firmware variant: 480x320 ILI9486 RPi-compatible (LIGHT_PIN=-1, SPI=20MHz)")
elseif(DEFINED LCD_480x320)
    target_compile_definitions(${COMPONENT_LIB} PUBLIC _480x320_LCD=1)
    message(STATUS "Firmware variant: 480x320 ST7796 (LIGHT_PIN=27)")
else()
    message(STATUS "Firmware variant: 320x240 ILI9341 (LIGHT_PIN=21)")
endif()

# Forward firmware version from CI cmake args (-DFIRMWARE_VERSION_MAJOR=x etc.)
# into C preprocessor so config.h #ifndef guards are overridden at build time.
foreach(VER_VAR FIRMWARE_VERSION_MAJOR FIRMWARE_VERSION_MINOR FIRMWARE_VERSION_PATCH FIRMWARE_BUILD_NUMBER)
    if(DEFINED ${VER_VAR})
        target_compile_definitions(${COMPONENT_LIB} PUBLIC ${VER_VAR}=${${VER_VAR}})
        message(STATUS "${VER_VAR} = ${${VER_VAR}}")
    endif()
endforeach()
