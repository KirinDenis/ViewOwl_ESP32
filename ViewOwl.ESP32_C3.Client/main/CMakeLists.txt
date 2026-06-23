# M2a: + WiFi station. SoC-agnostic logic is SHARED from the classic client by
# referencing the .c files directly (single source, no copy) — wifi_init.c and
# nvs_storage.c. Their headers come from the classic main/ include dir; config.h
# resolves from THIS dir first (listed first below), so each client keeps its own.
# serial_provision.c is NOT shared yet — it pulls in lcd_log.h (classic TUI); the
# C3 provisioning is adapted in M2b. Long-term (M5) the shared logic moves into a
# proper ESP-IDF component.
idf_component_register(
    SRCS "udp_client.c" "udp_auth.c" "main.c"
         "lcd_gc9a01.c"
         "provision.c"
         "../../ViewOwl.ESP32.Client/main/wifi_init.c"
         "../../ViewOwl.ESP32.Client/main/nvs_storage.c"
         "../../ViewOwl.ESP32.Client/main/frame_decoder.c"
         "../../ViewOwl.ESP32.Client/main/lz4_block.c"
         "../../ViewOwl.ESP32.Client/main/flash_frames.c"
    INCLUDE_DIRS "."
                 "../../ViewOwl.ESP32.Client/main"
    REQUIRES esp_driver_spi
             esp_driver_gpio
             esp_driver_i2c
             esp_driver_usb_serial_jtag
             esp_wifi
             esp_netif
             esp_event
             nvs_flash
             lwip
             esp_hw_support
             esp_timer
             esp_partition
             spi_flash
)
