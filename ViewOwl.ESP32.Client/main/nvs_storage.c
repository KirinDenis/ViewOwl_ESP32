#include "nvs_storage.h"

#include <string.h>
#include "nvs_flash.h"
#include "nvs.h"
#include "esp_log.h"

static const char *TAG = "nvs_storage";

esp_err_t nvs_storage_init(void)
{
    esp_err_t ret = nvs_flash_init();

    if (ret == ESP_ERR_NVS_NO_FREE_PAGES ||
        ret == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        /* Partition was truncated or has an incompatible version — erase and retry. */
        ESP_LOGW(TAG, "NVS partition needs erase (0x%x) — erasing", (unsigned)ret);
        ESP_ERROR_CHECK(nvs_flash_erase());
        ret = nvs_flash_init();
    }

    if (ret == ESP_OK) {
        ESP_LOGI(TAG, "NVS initialised");
    } else {
        ESP_LOGE(TAG, "NVS init failed: 0x%x", (unsigned)ret);
    }

    return ret;
}

bool nvs_storage_has_token(void)
{
    char buf[NVS_TOKEN_HEX_LEN + 1];
    return nvs_storage_read_token(buf, sizeof(buf)) == ESP_OK;
}

esp_err_t nvs_storage_read_token(char *buf, size_t buf_len)
{
    nvs_handle_t h;
    esp_err_t ret = nvs_open(NVS_VIEWOWL_NAMESPACE, NVS_READONLY, &h);
    if (ret != ESP_OK) {
        return ret;
    }

    ret = nvs_get_str(h, NVS_KEY_TOKEN, buf, &buf_len);
    nvs_close(h);
    return ret;
}

esp_err_t nvs_storage_write_token(const char *token_hex)
{
    nvs_handle_t h;
    esp_err_t ret = nvs_open(NVS_VIEWOWL_NAMESPACE, NVS_READWRITE, &h);
    if (ret != ESP_OK) {
        ESP_LOGE(TAG, "nvs_open failed: 0x%x", (unsigned)ret);
        return ret;
    }

    ret = nvs_set_str(h, NVS_KEY_TOKEN, token_hex);
    if (ret == ESP_OK) {
        ret = nvs_commit(h);
    }

    nvs_close(h);

    if (ret == ESP_OK) {
        ESP_LOGI(TAG, "Token written to NVS");
    } else {
        ESP_LOGE(TAG, "Token write failed: 0x%x", (unsigned)ret);
    }

    return ret;
}

bool nvs_storage_has_wifi(void)
{
    char buf[NVS_SSID_MAX_LEN + 1];
    return nvs_storage_read_ssid(buf, sizeof(buf)) == ESP_OK;
}

esp_err_t nvs_storage_read_ssid(char *buf, size_t buf_len)
{
    nvs_handle_t h;
    esp_err_t ret = nvs_open(NVS_VIEWOWL_NAMESPACE, NVS_READONLY, &h);
    if (ret != ESP_OK) {
        return ret;
    }
    ret = nvs_get_str(h, NVS_KEY_SSID, buf, &buf_len);
    nvs_close(h);
    return ret;
}

esp_err_t nvs_storage_read_pass(char *buf, size_t buf_len)
{
    nvs_handle_t h;
    esp_err_t ret = nvs_open(NVS_VIEWOWL_NAMESPACE, NVS_READONLY, &h);
    if (ret != ESP_OK) {
        return ret;
    }
    ret = nvs_get_str(h, NVS_KEY_PASS, buf, &buf_len);
    nvs_close(h);
    return ret;
}

esp_err_t nvs_storage_write_wifi(const char *ssid, const char *pass)
{
    nvs_handle_t h;
    esp_err_t ret = nvs_open(NVS_VIEWOWL_NAMESPACE, NVS_READWRITE, &h);
    if (ret != ESP_OK) {
        ESP_LOGE(TAG, "nvs_open failed: 0x%x", (unsigned)ret);
        return ret;
    }

    ret = nvs_set_str(h, NVS_KEY_SSID, ssid);
    if (ret == ESP_OK) {
        ret = nvs_set_str(h, NVS_KEY_PASS, pass);
    }
    if (ret == ESP_OK) {
        ret = nvs_commit(h);
    }

    nvs_close(h);

    if (ret == ESP_OK) {
        ESP_LOGI(TAG, "WiFi credentials written to NVS (ssid=%s)", ssid);
    } else {
        ESP_LOGE(TAG, "WiFi credentials write failed: 0x%x", (unsigned)ret);
    }

    return ret;
}

esp_err_t nvs_storage_write_batch_crc(uint32_t crc)
{
    nvs_handle_t h;
    esp_err_t ret = nvs_open(NVS_VIEWOWL_NAMESPACE, NVS_READWRITE, &h);
    if (ret != ESP_OK) {
        ESP_LOGE(TAG, "nvs_open failed (batch_crc write): 0x%x", (unsigned)ret);
        return ret;
    }

    ret = nvs_set_u32(h, NVS_KEY_BATCH_CRC, crc);
    if (ret == ESP_OK) {
        ret = nvs_commit(h);
    }

    nvs_close(h);

    if (ret == ESP_OK) {
        ESP_LOGI(TAG, "Batch CRC 0x%08X written to NVS", (unsigned)crc);
    } else {
        ESP_LOGE(TAG, "Batch CRC write failed: 0x%x", (unsigned)ret);
    }

    return ret;
}

esp_err_t nvs_storage_read_batch_crc(uint32_t *out_crc)
{
    nvs_handle_t h;
    esp_err_t ret = nvs_open(NVS_VIEWOWL_NAMESPACE, NVS_READONLY, &h);
    if (ret != ESP_OK) {
        return ret;
    }

    ret = nvs_get_u32(h, NVS_KEY_BATCH_CRC, out_crc);
    nvs_close(h);
    return ret;
}
