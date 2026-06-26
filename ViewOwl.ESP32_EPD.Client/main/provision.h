#pragma once

#include <stdbool.h>
#include <stdint.h>

/**
 * @brief Waits for device credentials sent by the browser over the S3 console
 *        (USB-Serial-JTAG). EPD port of the C3 provision_wait(): same line
 *        protocol (matches BurnModal.jsx), same NVS writes, reads from
 *        USB-Serial-JTAG. The e-paper panel is too slow to use as a live
 *        provisioning indicator, so there is no on-screen status here (unlike
 *        the C3, which draws to its round LCD).
 *
 * Protocol (lines, any order, all three required):
 *   VIEWOWL_TOKEN:<32-hex>\n
 *   VIEWOWL_WIFI_SSID:<ssid>\n
 *   VIEWOWL_WIFI_PASS:<password>\n
 *
 * Broadcasts "VIEWOWL_READY\n" every 3 s; on success stores all three to NVS,
 * sends "VIEWOWL_PROV_DONE\n", returns true. On timeout returns false.
 *
 * @param timeout_ms  Maximum time to wait.
 * @return true if all credentials were received and stored.
 */
bool provision_wait(uint32_t timeout_ms);
