#pragma once

#include <stdint.h>
#include <stdbool.h>
#include "esp_err.h"

#define NVS_VIEWOWL_NAMESPACE "viewowl"
#define NVS_KEY_TOKEN         "token"
#define NVS_KEY_SSID          "ssid"
#define NVS_KEY_PASS          "pass"
#define NVS_KEY_BATCH_CRC     "batchcrc"

/* 16 GUID bytes represented as 32 hex characters, no dashes, GUID byte order
 * (Data1/2/3 little-endian, Data4 big-endian — matches C# Guid.ToByteArray()). */
#define NVS_TOKEN_HEX_LEN  32
#define NVS_SSID_MAX_LEN   32   /* IEEE 802.11 SSID maximum length */
#define NVS_PASS_MAX_LEN   64   /* WPA2 passphrase maximum length  */

/**
 * @brief Initialises the NVS flash subsystem.
 *
 * Erases and reinitialises the NVS partition automatically when it is
 * corrupted or carries an incompatible layout version.
 * Must be called once at boot before any other nvs_storage_* call.
 *
 * @return ESP_OK on success.
 */
esp_err_t nvs_storage_init(void);

/**
 * @brief Returns true when a device token has been written to NVS.
 */
bool nvs_storage_has_token(void);

/**
 * @brief Returns true when WiFi credentials (SSID + password) are stored in NVS.
 */
bool nvs_storage_has_wifi(void);

/**
 * @brief Reads the stored token as a 32-character hex string (GUID byte order).
 *
 * @param buf      Destination buffer — must be at least NVS_TOKEN_HEX_LEN + 1 bytes.
 * @param buf_len  Size of buf in bytes.
 * @return ESP_OK on success, ESP_ERR_NVS_NOT_FOUND if no token stored yet.
 */
esp_err_t nvs_storage_read_token(char *buf, size_t buf_len);

/**
 * @brief Writes a 32-character hex token (GUID byte order) to NVS and commits.
 *
 * @param token_hex  Exactly NVS_TOKEN_HEX_LEN ASCII hex characters, null-terminated.
 * @return ESP_OK on success.
 */
esp_err_t nvs_storage_write_token(const char *token_hex);

/**
 * @brief Reads the stored WiFi SSID from NVS.
 *
 * @param buf      Destination buffer — must be at least NVS_SSID_MAX_LEN + 1 bytes.
 * @param buf_len  Size of buf in bytes.
 * @return ESP_OK on success, ESP_ERR_NVS_NOT_FOUND if not stored yet.
 */
esp_err_t nvs_storage_read_ssid(char *buf, size_t buf_len);

/**
 * @brief Reads the stored WiFi password from NVS.
 *
 * @param buf      Destination buffer — must be at least NVS_PASS_MAX_LEN + 1 bytes.
 * @param buf_len  Size of buf in bytes.
 * @return ESP_OK on success, ESP_ERR_NVS_NOT_FOUND if not stored yet.
 */
esp_err_t nvs_storage_read_pass(char *buf, size_t buf_len);

/**
 * @brief Writes WiFi SSID and password to NVS in a single commit.
 *
 * @param ssid  NUL-terminated SSID string (max NVS_SSID_MAX_LEN chars).
 * @param pass  NUL-terminated password string (max NVS_PASS_MAX_LEN chars).
 * @return ESP_OK on success.
 */
esp_err_t nvs_storage_write_wifi(const char *ssid, const char *pass);

/**
 * @brief Persists the Class-C batch CRC32 to NVS so that the next boot can
 *        restore it into current_frame_crc and send a non-zero HELLO, enabling
 *        the server to return NOT_MODIFIED instead of triggering a full BATCH
 *        re-download when the frames in flash are still current.
 *
 * Write 0 when the flash partition has been erased (failed BATCH transfer) so
 * the next HELLO correctly reports "no frame" and forces a fresh download.
 *
 * @param crc  CRC32 of the last successfully committed batch (0 to clear).
 * @return ESP_OK on success.
 */
esp_err_t nvs_storage_write_batch_crc(uint32_t crc);

/**
 * @brief Reads the persisted batch CRC32 from NVS.
 *
 * @param out_crc  Destination for the stored CRC value.
 * @return ESP_OK on success, ESP_ERR_NVS_NOT_FOUND if never written.
 */
esp_err_t nvs_storage_read_batch_crc(uint32_t *out_crc);
