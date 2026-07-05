# ViewOwl ESP32-S3 e-paper client. SoC-agnostic logic (WiFi, NVS) is SHARED from the
# classic client by referencing the .c files directly (single source, no copy) — the
# same pattern the C3 client uses. config.h resolves from THIS dir first (listed first
# below) so each client keeps its own. The EPD driver (epd_4in2.c) is client-specific.
# Serial provisioning (provision.c) reads the browser's creds over USB-Serial-JTAG,
# the same custom protocol the C3 and classic clients use.
# Panel driver is selected by the EPD_792x272 build flag (-DEPD_792x272=1 from CI /
# idf.py): default = 4.2" 400x300 (epd_4in2.c), wide = 5.79" 792x272 cascade (epd_wide.c).
# Both expose the same API via epd_panel.h, so the rest of the client is panel-agnostic.
if(EPD_792x272)
    set(EPD_DRIVER_SRC "epd_wide.c")
else()
    set(EPD_DRIVER_SRC "epd_4in2.c")
endif()

idf_component_register(
    SRCS "provision.c" "epd_text.c" "main.c"
         "${EPD_DRIVER_SRC}"
         "udp_client.c"
         "../../ViewOwl.ESP32.Client/main/wifi_init.c"
         "../../ViewOwl.ESP32.Client/main/nvs_storage.c"
    INCLUDE_DIRS "."
                 "../../ViewOwl.ESP32.Client/main"
    REQUIRES esp_driver_gpio
             esp_driver_usb_serial_jtag
             esp_wifi
             esp_netif
             esp_event
             nvs_flash
             lwip
             esp_hw_support
             esp_timer
)

# Propagate the build flag to the compiler as a C #define (a bare CMake -D cache var does
# NOT reach the source otherwise — the lesson from the 480x320 variant).
if(EPD_792x272)
    target_compile_definitions(${COMPONENT_LIB} PRIVATE EPD_792x272=1)
endif()
