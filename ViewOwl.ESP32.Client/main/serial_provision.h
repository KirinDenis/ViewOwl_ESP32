#pragma once

#include <stdbool.h>
#include <stdint.h>
#include "nvs_storage.h"

/**
 * @brief Waits for device credentials sent by the browser over UART0.
 *
 * Broadcasts "VIEWOWL_READY\n" every 3 seconds so the browser can detect
 * when the device is listening.  Blocks until all three required fields are
 * received and stored to NVS, or until @p timeout_ms elapses.
 *
 * Expected serial protocol (lines in any order, all three required):
 *   VIEWOWL_TOKEN:<32-hex-chars>\n   — device token (GUID byte order, no dashes)
 *   VIEWOWL_SSID:<ssid>\n            — WiFi SSID (max NVS_SSID_MAX_LEN chars)
 *   VIEWOWL_PASS:<password>\n        — WiFi password (max NVS_PASS_MAX_LEN chars)
 *
 * On success all three values are written to NVS atomically and
 * "VIEWOWL_PROVISION_OK\n" is sent back, then the function returns true.
 * On timeout "VIEWOWL_TOKEN_ERR:timeout\n" is sent and false is returned.
 * Validation errors send a per-field error response.
 *
 * @param timeout_ms  Maximum time to wait in milliseconds.
 * @return true if all credentials were received and stored successfully.
 */
bool serial_provision_wait(uint32_t timeout_ms);
