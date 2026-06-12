#pragma once
#include <stdint.h>
#include <string.h>

#define PACKET_SIZE 1400
#define MAGIC_TOKEN 0xABADBABE

typedef enum {
    PACKET_HELLO        = 1,    /* device identifies itself; payload = token string */
    PACKET_AUTH         = 2,    /* server acknowledges and assigns session id */
    PACKET_NOT_MODIFIED = 3,    /* server: frame unchanged since last download — skip transfer */
    PACKET_DATA         = 4,    /* server sends a frame chunk */
    PACKET_BATCH_START  = 5,    /* server: beginning Class-C multi-frame transfer; payload = batch_start_payload_t */
    PACKET_BATCH_COMMIT = 6,    /* server: all frames stored in flash, start looped playback */
    PACKET_ACK          = 8,    /* device acknowledges a DATA or PING packet */
    PACKET_DONE         = 16,   /* server signals end of frame transfer */
    PACKET_ERROR        = 32,   /* error / rejection */
    PACKET_PING         = 64,   /* device heartbeat (uptime, free heap, Wi-Fi RSSI) */
    PACKET_CONFIG       = 128   /* server pushes configuration to device */
} packet_type_t;

typedef enum {
    FLAG_LAST       = 0x0001,
    FLAG_RETRANSMIT = 0x0002,
    FLAG_TIMEOUT    = 0x0004,
    FLAG_BADPACKET  = 0x0008,
    FLAG_BYE        = 0x0010,
    FLAG_RESTART    = 0x0020  /* server instructs device to reboot after CONFIG is applied */
} packet_flags_t;

#pragma pack(push, 1)

typedef struct {
    uint32_t magic;          /* MAGIC_TOKEN — filters out alien packets */
    uint8_t  packet_type;    /* see packet_type_t */
    uint16_t session_id;     /* assigned at AUTH, must match on all subsequent packets */
    uint32_t seq;            /* chunk index for DATA / ACK pairs */
    uint16_t payload_length; /* bytes of payload following this header */
    uint16_t flags;          /* see packet_flags_t */
} packet_header_t;

/**
 * @brief Heartbeat payload sent by the device in every PACKET_PING.
 *
 * Must stay binary-identical to PingPacketPayload (ViewOwl.UDP.Utils, C#).
 * Total size: 10 bytes.
 */
typedef struct {
    uint32_t uptime;     /* device uptime in seconds */
    uint32_t free_heap;  /* current free heap in bytes */
    int16_t  wifi_rssi;  /* Wi-Fi signal strength in dBm (negative value) */
    uint16_t reserved;   /* padding — must be zero */
} ping_payload_t;

/**
 * @brief Configuration payload pushed by the server in PACKET_CONFIG.
 *
 * Must stay binary-identical to ConfigPacketPayload (ViewOwl.UDP.Utils, C#).
 * Total size: 12 bytes.
 * When FLAG_RESTART is set in the header flags, the device must call esp_restart()
 * after processing this packet.
 */
typedef struct {
    uint16_t ping_interval_s; /* new PING heartbeat interval in seconds (0 = keep current) */
    uint16_t reserved1;       /* padding — must be zero */
    uint32_t reserved2;       /* reserved for future use — must be zero */
    uint32_t reserved3;       /* reserved for future use — must be zero */
} config_payload_t;

/**
 * @brief Extended HELLO payload sent by the device on first connection.
 *
 * Carries device capabilities so the server can update the database (display type,
 * resolution, firmware version) without a separate configuration call.
 *
 * Must stay binary-identical to HelloPacketPayload (ViewOwl.UDP.Utils, C#,
 * LayoutKind.Sequential Pack=1).  Total size: 31 bytes.
 *
 * The @c token field holds the device auth GUID in Windows binary GUID format
 * (Data1/Data2/Data3 stored little-endian, Data4 big-endian) so the C# server
 * can deserialise it with a direct struct read.
 */
typedef struct {
    uint8_t  token[16];               /* device auth token — 16-byte Windows GUID */
    uint8_t  display_type_id;         /* DisplayType: 0=Unknown 1=ST7789 2=ILI9341 3=ST7796 */
    uint16_t display_width;           /* display width in pixels  */
    uint16_t display_height;          /* display height in pixels */
    uint8_t  firmware_version_major;  /* firmware major version */
    uint8_t  firmware_version_minor;  /* firmware minor version */
    uint8_t  firmware_version_patch;  /* firmware patch version */
    uint8_t  reserved;                /* padding — must be zero */
    uint8_t  mac[6];                  /* WiFi MAC from esp_read_mac(MAC_WIFI_STA) — device fingerprint */
} hello_payload_t; /* 31 bytes */

#pragma pack(pop)

/**
 * @brief Payload carried in PACKET_BATCH_START.
 *
 * Tells the device how many frames will follow and at what FPS to play them back.
 * The server sends all frames concatenated as ordinary DATA packets immediately
 * after BATCH_START is ACK-ed.  frame_sizes[] lets the device split the byte
 * stream into individual frames when writing to the flash partition.
 *
 * Must stay binary-identical to BatchStartPayload (ViewOwl.UDP.Utils, C#).
 * Total size: 4 + BATCH_MAX_FRAMES * 4 = 68 bytes.
 */
#define BATCH_MAX_FRAMES 16

typedef struct {
    uint8_t  frame_count;                   /* number of frames  (1..BATCH_MAX_FRAMES) */
    uint8_t  fps;                           /* playback frame-rate in frames/second     */
    uint16_t reserved;                      /* padding — must be zero                  */
    uint32_t frame_sizes[BATCH_MAX_FRAMES]; /* compressed byte length of each frame    */
} batch_start_payload_t; /* 68 bytes */

/* Error codes sent as a single byte in PACKET_ERROR payload.
 * Must stay in sync with ErrorCodes enum in ViewOwl.UDP.Utils (C#). */
#define ERR_BAD_TOKEN       0x01  /* HELLO payload could not be parsed as a token */
#define ERR_UNKNOWN_DEVICE  0x02  /* token not registered in the database */
#define ERR_NO_TEMPLATE     0x03  /* device has no active template assigned */
#define ERR_NO_FRAME        0x04  /* no successful grab available for the template */

// -------- Helpers --------

static inline int write_packet(uint8_t *buf, packet_header_t *hdr, const uint8_t *payload, size_t payload_len) {
    if (!buf || !hdr) return -1;
    if (payload && payload_len + sizeof(packet_header_t) > PACKET_SIZE) return -2;

    hdr->payload_length = (uint16_t)(payload ? payload_len : 0);
    memcpy(buf, hdr, sizeof(packet_header_t));
    if (payload && payload_len > 0) {
        memcpy(buf + sizeof(packet_header_t), payload, payload_len);
    }
    return (int)(sizeof(packet_header_t) + hdr->payload_length);
}

static inline int read_header(const uint8_t *buf, size_t len, packet_header_t *hdr) {
    if (!buf || len < sizeof(packet_header_t)) return -1;
    memcpy(hdr, buf, sizeof(packet_header_t));
    if (hdr->magic != MAGIC_TOKEN) return -2;
    return 0;
}