# Firmware Reference

**Language:** C99  
**Framework:** ESP-IDF 5.5  
**Build system:** CMake (ESP-IDF native)  
**Directory:** [`ViewOwl.ESP32.Client/main/`](../../ViewOwl.ESP32.Client/main/) (classic rectangular boards), [`ViewOwl.ESP32_C3.Client/main/`](../../ViewOwl.ESP32_C3.Client/main/) (round display)

Rectangular variants are built from the classic source tree:
- **320×240** — ILI9341 driver, GPIO 21 backlight (default build)
- **480×320** — ST7796 driver, GPIO 27 backlight (`-DLCD_480x320=1`)

The variant is selected at compile time via a CMake flag that must appear in **both** `CMakeLists.txt` (cmake level) and `main/CMakeLists.txt` (`target_compile_definitions`) — a bug where it only appeared at the cmake level caused both variants to silently build as ILI9341 for months.

### Round display — separate ESP32-C3 client

The 240×240 round GC9A01 display has its **own firmware client**, [`ViewOwl.ESP32_C3.Client`](../../ViewOwl.ESP32_C3.Client/), because it runs on an ESP32-C3 (RISC-V, single core, 2 MB flash, no PSRAM) rather than the classic ESP32. The SoC-agnostic logic — UDP protocol, frame decode, WiFi, NVS — is **shared** from the classic client; only the LCD layer and board config are C3-specific. The wire protocol (`packet.h`) is byte-for-byte identical across both clients, so the server treats every device the same. Class C animation playback works on the round too: the frame batch streams to a dedicated flash partition and plays from flash (the whole batch does not fit the C3's RAM), so playback survives a network drop.

### `display-types.json` — display registry (single source of truth)

**File:** [`display-types.json`](../../display-types.json) (repo root)

Every display type is described once in this registry (`firmwareFamilies` + `displayTypes`). It drives, with no drift between them:
- per-display-type firmware version checks on the server,
- the CI firmware build,
- the esp-web-tools manifests used by the browser flasher,
- the flasher UI target list.

To add a future display, you add one entry here. Current `DisplayType` ids: `ILI9341 = 2`, `ST7796 = 3`, `ILI9486 = 4`, `GC9A01 = 5` (the round display).

---

## Module overview

### `main.c` / `main.cpp` — Entry point

Initialises NVS, WiFi, and LCD; spawns `udp_client_task` as a FreeRTOS task. Also runs the TUI boot screen while WiFi connects.

**10-minute hardware WDT** is started here and fed by `udp_client.c` on any successful server exchange. If the UDP loop stalls for 10 minutes without feeding the WDT, the device reboots automatically — self-healing without human intervention.

---

### `config.h` — Compile-time constants

**File:** [`config.h`](../../ViewOwl.ESP32.Client/main/config.h)

All device-specific constants are in one place. Key values:

| Constant | Value | Purpose |
|---|---|---|
| `SERVER_IP` | `"YOUR_SERVER_IP"` | Grabber server — set before flashing |
| `SERVER_PORT` | `11000` | UDP port |
| `WIFI_SSID` / `WIFI_PASS` | provisioned | Overwritten by serial provisioning |
| `TOKEN` | per-variant GUID string | Device identity sent in HELLO |
| `TOKEN_BYTES` | binary GUID | Same GUID in wire format |
| `FLASH_THRESHOLD` | `90 × 1024` | Frames above this go to flash partition |
| `BATCH_IDLE_S` | `300` | Seconds between HELLOs after batch commit |
| `BATCH_RETRY_DELAY_S` | `30` | Back-off after failed batch |
| `HELLO_FAIL_MAX` | `15` | Consecutive HELLO failures before socket recreation |
| `ACK_RETRY_MAX` | `3` | ACK retries on last DATA packet |
| `PING_INTERVAL_S` | `30` | Heartbeat interval during idle |
| `LCD_H_RES` / `LCD_V_RES` | variant-dependent | Display resolution |
| `LIGHT_PIN` | 27 or 21 | Backlight GPIO |

See [`docs/hardware.md`](../hardware.md) for why `FLASH_THRESHOLD` is 90 KB and why packet size is 1400 bytes.

---

### `udp_client.c` — UDP protocol client

**File:** [`udp_client.c`](../../ViewOwl.ESP32.Client/main/udp_client.c)

The core of the firmware. See the full protocol description in [`docs/reference/udp-transport.md`](udp-transport.md).

**FreeRTOS task:** `udp_client_task` — single task, runs in a `while(1)` loop.

**Key state variables:**

| Variable | Purpose |
|---|---|
| `current_frame_crc` | CRC32 of last committed frame; sent in every HELLO; restored from NVS on boot |
| `after_batch_commit` | `true` after BATCH_COMMIT or when valid flash frames exist at boot; controls idle duration |
| `batch_failed` | `true` when a BATCH transfer aborted mid-way; activates 30-second back-off |
| `not_modified_no_batch_count` | Self-healing counter: forces CRC=0 after ~5 min of NOT_MODIFIED in single-frame mode |
| `hello_fail_count` | Counts consecutive HELLO timeouts; triggers socket recreation at `HELLO_FAIL_MAX` |
| `last_server_ok_us` | Timestamp of last server response; used to show "NO SERVER" hint after 30 s |

---

### `lcd_init.c` — SPI LCD initialisation

**File:** [`lcd_init.c`](../../ViewOwl.ESP32.Client/main/lcd_init.c)

Initialises the SPI2 bus and the display controller. Key parameters:

| Parameter | Value |
|---|---|
| SPI host | `SPI2_HOST` |
| SCLK | GPIO 14 |
| MOSI | GPIO 13 |
| MISO | GPIO 12 |
| DC | GPIO 2 |
| CS | GPIO 15 |
| Clock speed | **50 MHz** |
| Max transfer | `LCD_BUFFER_SIZE` (80-line strip) |

The 80-line strip buffer limits peak DRAM usage to ~76 KB (480×320) or ~51 KB (320×240) rather than holding the entire frame in RAM. See [`docs/hardware.md`](../hardware.md) — "ESP32 memory layout".

The display controller is selected at compile time:
- `#ifdef _480x320_LCD` → `esp_lcd_st7796.h`
- `#else` → `esp_lcd_ili9341.h`

---

### `nvs_storage.c` / `nvs_storage.h` — Non-volatile storage

**Files:** [`nvs_storage.c`](../../ViewOwl.ESP32.Client/main/nvs_storage.c), [`nvs_storage.h`](../../ViewOwl.ESP32.Client/main/nvs_storage.h)

Wraps ESP-IDF NVS (flash-backed key-value store). All keys live under the `viewowl` namespace.

| Key | Type | Purpose |
|---|---|---|
| `ssid` | string | WiFi network name |
| `password` | string | WiFi password |
| `batchcrc` | `uint32` | CRC of last committed batch — survives reboots |

**Why NVS for batch CRC?** Without it, every reboot resets `current_frame_crc = 0`, causing the server to respond with a full BATCH re-download even when the flash partition already holds the correct frames. Writing the CRC to NVS after `BATCH_COMMIT` and restoring it on boot eliminates this 27-second freeze. See the full explanation in [`docs/reference/udp-transport.md`](udp-transport.md) — "CRC persistence across reboots".

---

### `wifi_init.c` — WiFi station

**File:** [`wifi_init.c`](../../ViewOwl.ESP32.Client/main/wifi_init.c)

Standard ESP-IDF WiFi station setup. Reads SSID and password from NVS. `WIFI_MAXIMUM_RETRY` (5) attempts before giving up and waiting for serial re-provisioning.

Uses `esp_event_loop` for WiFi and IP events — no raw FreeRTOS queues. Connection state is signalled to `main.c` via an `EventGroupHandle_t`.

---

### `flash_frames.c` — Flash partition player

**File:** [`flash_frames.c`](../../ViewOwl.ESP32.Client/main/flash_frames.c)

Manages the `frames` flash partition — a dedicated OTA-style partition that stores Class C animation frames. Frames are written during BATCH transfer and read back sequentially during playback.

Key functions:

| Function | Purpose |
|---|---|
| `flash_frames_valid()` | Returns `true` if the partition contains a committed batch |
| `flash_frames_write_chunk()` | Appends a DATA chunk to the partition during BATCH receive |
| `flash_frames_commit()` | Marks the batch as complete (atomic; survives power loss) |
| `flash_frames_read_frame()` | Memory-mapped read of frame N for LCD rendering |
| `flash_frames_erase()` | Erases the partition before a new BATCH |

The "frames" partition label is defined as `FRAMES_PARTITION_LABEL` in `config.h`. The partition table entry must be present in `partitions.csv`.

---

### `packet.h` — Protocol wire format

**File:** [`packet.h`](../../ViewOwl.ESP32.Client/main/packet.h)

C mirror of [`UDPUtils.cs`](../../ViewOwl.UDP.Utils/UDPUtils.cs). Defines `packet_header_t` as a packed struct, all `PACKET_*` type constants, `FLAG_*` bit flags, and every payload struct.

**Must stay byte-for-byte identical to the C# definition.** Any divergence causes silent protocol failures — the magic number check (`0xABADBABE`) will reject packets that happen to have different offsets.

#### `packet_header_t` — 15 bytes, `#pragma pack(1)`

| Field | Type | Offset | Notes |
|---|---|---|---|
| `magic` | `uint32_t` | 0 | `0xABADBABE` — filters alien packets |
| `packet_type` | `uint8_t` | 4 | `packet_type_t` enum value |
| `session_id` | `uint16_t` | 5 | Assigned at AUTH; must match on all subsequent packets |
| `seq` | `uint32_t` | 7 | Chunk index for DATA/ACK pairs |
| `payload_length` | `uint16_t` | 11 | Bytes of payload following the header |
| `flags` | `uint16_t` | 13 | `FLAG_LAST`, `FLAG_RETRANSMIT`, `FLAG_BYE`, `FLAG_RESTART`, … |

#### `ping_payload_t` — 10 bytes

Carried in `PACKET_PING`. Must match `PingPacketPayload` in C#.

| Field | Type | Notes |
|---|---|---|
| `uptime` | `uint32_t` | Device uptime in seconds |
| `free_heap` | `uint32_t` | Current free heap in bytes |
| `wifi_rssi` | `int16_t` | WiFi signal strength in dBm (negative) |
| `reserved` | `uint16_t` | Padding — must be zero |

#### `hello_payload_t` — 31 bytes

Extended HELLO payload — sent once at first connection so the server can update display type, resolution, firmware version, and MAC without a separate call. Must match `HelloPacketPayload` in C#.

| Field | Type | Bytes | Notes |
|---|---|---|---|
| `token` | `uint8_t[16]` | 16 | Device auth GUID in Windows binary GUID layout |
| `display_type_id` | `uint8_t` | 1 | `0=Unknown, 1=ST7789, 2=ILI9341, 3=ST7796, 4=ILI9486, 5=GC9A01 (round)` |
| `display_width` | `uint16_t` | 2 | Pixels |
| `display_height` | `uint16_t` | 2 | Pixels |
| `firmware_version_major` | `uint8_t` | 1 | |
| `firmware_version_minor` | `uint8_t` | 1 | |
| `firmware_version_patch` | `uint8_t` | 1 | |
| `reserved` | `uint8_t` | 1 | Padding — must be zero |
| `mac` | `uint8_t[6]` | 6 | WiFi MAC from `esp_read_mac(MAC_WIFI_STA)` |

#### `config_payload_t` — 12 bytes

Carried in `PACKET_CONFIG`. When `FLAG_RESTART` is set, the device calls `esp_restart()` after applying. Must match `ConfigPacketPayload` in C#.

| Field | Type | Notes |
|---|---|---|
| `ping_interval_s` | `uint16_t` | New PING heartbeat interval in seconds (`0` = keep current) |
| `reserved1` | `uint16_t` | Padding — must be zero |
| `reserved2` | `uint32_t` | Reserved for future use |
| `reserved3` | `uint32_t` | Reserved for future use |

#### `batch_start_payload_t` — 68 bytes

Carried in `PACKET_BATCH_START`. Tells the device how many animation frames will follow and at what FPS to play them. Must match `BatchStartPayload` in C#.

| Field | Type | Notes |
|---|---|---|
| `frame_count` | `uint8_t` | Number of frames (`1..BATCH_MAX_FRAMES=16`) |
| `fps` | `uint8_t` | Playback rate in frames/second |
| `reserved` | `uint16_t` | Padding — must be zero |
| `frame_sizes[16]` | `uint32_t[16]` | Compressed byte length of each frame |

#### Error codes (single byte in `PACKET_ERROR` payload)

| Constant | Value | Meaning |
|---|---|---|
| `ERR_BAD_TOKEN` | `0x01` | HELLO payload could not be parsed as a token |
| `ERR_UNKNOWN_DEVICE` | `0x02` | Token not registered in the database |
| `ERR_NO_TEMPLATE` | `0x03` | Device has no active template assigned |
| `ERR_NO_FRAME` | `0x04` | No successful grab available for the template |

---

## Build variants and CI

Two binaries are built by CI on every push to `dev`:

```yaml
# CI firmware build pipeline (simplified)
- name: Build 320×240
  run: idf.py build

- name: Build 480×320
  run: idf.py -DLCD_480x320=1 build
```

The round ESP32-C3 client is built alongside them as its own target. Which firmware is built for which display is driven by `display-types.json` (see above), so the build list and the flasher manifests never drift.

All binaries are uploaded as release assets and referenced by the web flash manifest. The landing page flash wizard selects the correct binary based on the display chosen by the user — including the **240 × 240 round** target.

---

## Flashing and provisioning

Devices are flashed from the browser using `esp-web-tools` (Web Serial API). No drivers or local toolchain required.

After flashing, the firmware opens a serial terminal for WiFi provisioning. SSID and password are written to NVS and survive subsequent OTA updates.

See [`docs/getting-started.md`](../getting-started.md) for the end-user flow.
