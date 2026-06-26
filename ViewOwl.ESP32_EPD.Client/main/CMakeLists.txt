# ViewOwl ESP32-S3 e-paper client. SoC-agnostic logic (WiFi, NVS) is SHARED from the
# classic client by referencing the .c files directly (single source, no copy) — the
# same pattern the C3 client uses. config.h resolves from THIS dir first (listed first
# below) so each client keeps its own. The EPD driver (epd_4in2.c) is client-specific.
# Serial provisioning (provision.c) reads the browser's creds over USB-Serial-JTAG,
# the same custom protocol the C3 and classic clients use.
idf_component_register(
    SRCS "provision.c" "epd_text.c" "main.c"
         "epd_4in2.c"
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
