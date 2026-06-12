# ViewOwl UDP Frame Delivery Protocol

**Status:** Informational  
**Version:** 1.3  
**Port:** 11000/UDP  
**Implementations:** `ViewOwl.UDP.Server` (C# .NET 8), `ViewOwl.ESP32.Client` (C, ESP-IDF 5.5)

---

## Table of Contents

1. [Introduction](#1-introduction)
2. [Conventions](#2-conventions)
3. [Packet Structure](#3-packet-structure)
   - 3.1 [Header](#31-header)
   - 3.2 [Packet Types](#32-packet-types)
   - 3.3 [Flags](#33-flags)
   - 3.4 [Payloads](#34-payloads)
4. [Session Model](#4-session-model)
5. [Protocol Flows](#5-protocol-flows)
   - 5.1 [Normal Frame Delivery](#51-normal-frame-delivery)
   - 5.2 [CRC Dedup — Frame Not Modified](#52-crc-dedup--frame-not-modified)
   - 5.3 [Delta Delivery](#53-delta-delivery)
   - 5.4 [Class-C Batch Transfer](#54-class-c-batch-transfer)
   - 5.5 [Server-Initiated Refresh (Push)](#55-server-initiated-refresh-push)
   - 5.6 [Device Heartbeat (PING)](#56-device-heartbeat-ping)
   - 5.7 [Server Config Push](#57-server-config-push)
   - 5.8 [Error Handling](#58-error-handling)
6. [CRC Algorithm](#6-crc-algorithm)
7. [Frame File Formats](#7-frame-file-formats)
8. [Timing and Retransmission](#8-timing-and-retransmission)
9. [Error Codes](#9-error-codes)
10. [Implementation Notes](#10-implementation-notes)

---

## 1. Introduction

The ViewOwl UDP Frame Delivery Protocol transfers pre-rendered BGR565 bitmap
frames from a server to ESP32-based LCD displays over UDP/Wi-Fi.

The protocol is designed for:

- **Low-resource clients** — ESP32 with limited RAM and no OS TCP/IP stack overhead.
- **Lossy networks** — home Wi-Fi. Reliability is provided by an application-layer
  stop-and-wait acknowledgement scheme layered on top of UDP.
- **Bandwidth efficiency** — CRC-based deduplication avoids retransmitting frames
  that the device already has. Delta and LZ4-compressed transfers reduce payload size.
- **Multi-frame animation** — Class-C batch transfer delivers up to 16 frames to
  the device's flash for autonomous looped playback.

### Design decisions

| Decision | Rationale |
|---|---|
| UDP, not TCP | Lower overhead on embedded side; retransmit logic is simpler than TCP for this use case |
| Stop-and-wait ACK | Simple to implement on MCU; acceptable throughput for ≤300 KB frames |
| Magic bytes on every packet | Drops unrelated broadcast/multicast traffic without parsing |
| CRC dedup before transfer | Eliminates most transfers when template content has not changed |
| Application-layer session ID | Multiple devices share one socket; session ID routes packets to the right session thread |

---

## 2. Conventions

All multi-byte integer fields are **little-endian** unless stated otherwise.

```
Device  →  Server    Messages sent by the ESP32 firmware
Server  →  Device    Messages sent by the UDP server
[n]                  Field width in bytes
MSB                  Most significant byte
LSB                  Least significant byte
0x                   Hexadecimal prefix
```

---

## 3. Packet Structure

Every packet — in both directions — starts with a fixed 15-byte header followed
by an optional variable-length payload.

```
 0               1               2               3
 0 1 2 3 4 5 6 7 0 1 2 3 4 5 6 7 0 1 2 3 4 5 6 7 0 1 2 3 4 5 6 7
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                         Magic (32-bit)                        |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  PacketType   |         SessionId (16-bit)    |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                          Seq (32-bit)                         |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|       PayloadLength (16-bit)  |         Flags (16-bit)        |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                    Payload (0..1385 bytes)                    |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

Maximum packet size: **1400 bytes** (header 15 B + payload up to 1385 B).

### 3.1 Header

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| 0 | 4 | `Magic` | Fixed value `0xABADBABE` (LE). Packets not matching this value are silently discarded. |
| 4 | 1 | `PacketType` | Identifies the message type. See §3.2. |
| 5 | 2 | `SessionId` | Session identifier assigned by the server at AUTH. `0` before session establishment. |
| 7 | 4 | `Seq` | Chunk sequence number for DATA/ACK pairs. Reused as frame CRC in HELLO (see §6). |
| 11 | 2 | `PayloadLength` | Length of the payload following this header in bytes. `0` for header-only packets. |
| 13 | 2 | `Flags` | Bitmask of control flags. See §3.3. |

**Total header size: 15 bytes.**

The header struct is packed with no alignment padding (`#pragma pack(1)` in C,
`LayoutKind.Sequential Pack=1` in C#). Both implementations must read/write it
with explicit byte-order handling — never via a cast on a platform that may add padding.

### 3.2 Packet Types

| Value | Name | Direction | Description |
|-------|------|-----------|-------------|
| 1 | `HELLO` | Device → Server | Device identifies itself and requests a frame. |
| 2 | `AUTH` | Server → Device | Server accepts the device and assigns a session ID. Also used as a trigger packet (§5.5). |
| 3 | `NOT_MODIFIED` | Server → Device | CRC matches — device already has the current frame. |
| 4 | `DATA` | Server → Device | One chunk of frame data. |
| 5 | `BATCH_START` | Server → Device | Announces a Class-C multi-frame batch transfer. |
| 6 | `BATCH_COMMIT` | Server → Device | All batch frames written; begin looped playback. |
| 8 | `ACK` | Device → Server | Acknowledges a DATA chunk or a PING heartbeat. |
| 16 | `DONE` | Server → Device | End of frame transfer. |
| 32 | `ERROR` | Server → Device | Rejection with optional error code. |
| 64 | `PING` | Device → Server | Heartbeat carrying uptime, free heap, and Wi-Fi RSSI. |
| 128 | `CONFIG` | Server → Device | Server pushes configuration values to the device. |

### 3.3 Flags

Flags are a 16-bit bitmask in the `Flags` header field.

| Bit | Value | Name | Description |
|-----|-------|------|-------------|
| 0 | `0x0001` | `LAST` | Set on the final DATA chunk of a frame. |
| 1 | `0x0002` | `RETRANSMIT` | Set when a DATA chunk is resent after ACK timeout. |
| 2 | `0x0004` | `TIMEOUT` | Server signals ACK timeout to the device. |
| 3 | `0x0008` | `BADPACKET` | Packet failed header validation (magic mismatch, truncated). |
| 4 | `0x0010` | `BYE` | Sender is closing the session. |
| 5 | `0x0020` | `RESTART` | Set in CONFIG packets — device must call `esp_restart()` after processing. |

### 3.4 Payloads

#### HELLO Payload — `hello_payload_t` (31 bytes)

Sent by the device in every HELLO packet.

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| 0 | 16 | `token` | Device authentication token — 16-byte Windows GUID (binary format). Data1/Data2/Data3 stored LE, Data4 BE. |
| 16 | 1 | `display_type_id` | Display controller: `0`=Unknown, `1`=ST7789, `2`=ILI9341, `3`=ST7796. |
| 17 | 2 | `display_width` | Horizontal resolution in pixels. |
| 19 | 2 | `display_height` | Vertical resolution in pixels. |
| 21 | 1 | `firmware_version_major` | Firmware major version. |
| 22 | 1 | `firmware_version_minor` | Firmware minor version. |
| 23 | 1 | `firmware_version_patch` | Firmware patch version. |
| 24 | 1 | `reserved` | Must be zero. |
| 25 | 6 | `mac` | Wi-Fi station MAC address from `esp_read_mac()`. Used as device fingerprint. |

The HELLO `Seq` field carries the **standard CRC32** of the last frame the device
rendered (see §6). Value `0` means the device has no frame yet.

#### PING Payload — `ping_payload_t` (10 bytes)

Sent by the device in every PING heartbeat.

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| 0 | 4 | `uptime` | Device uptime in seconds since last boot. |
| 4 | 4 | `free_heap` | Current free heap in bytes. |
| 8 | 2 | `wifi_rssi` | Wi-Fi signal strength in dBm (signed, negative). |
| 10 | 2 | `reserved` | Must be zero. |

#### BATCH_START Payload — `batch_start_payload_t` (68 bytes)

Announces a Class-C multi-frame batch transfer.

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| 0 | 1 | `frame_count` | Number of frames in this batch (1..16). |
| 1 | 1 | `fps` | Playback frame rate in frames per second. |
| 2 | 2 | `reserved` | Must be zero. |
| 4 | 64 | `frame_sizes[16]` | Compressed byte length of each frame (uint32 LE each). Entries beyond `frame_count` must be zero. |

#### CONFIG Payload — `config_payload_t` (12 bytes)

Pushed by the server to change device operating parameters.

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| 0 | 2 | `ping_interval_s` | New PING heartbeat interval in seconds. `0` = keep current. |
| 2 | 2 | `reserved1` | Must be zero. |
| 4 | 4 | `reserved2` | Reserved for future use. Must be zero. |
| 8 | 4 | `reserved3` | Reserved for future use. Must be zero. |

If `FLAG_RESTART` is set in the CONFIG packet header, the device calls
`esp_restart()` immediately after applying the configuration.

#### ERROR Payload (1 byte, optional)

Single byte error code. See §9.

---

## 4. Session Model

A **session** is a single frame transfer from server to one device.

- The server maintains one UDP socket on port 11000.
- Multiple devices share the same socket. The server routes incoming packets
  by source `(IP, port)` for HELLO and by `SessionId` for all subsequent packets.
- Session IDs are 16-bit integers assigned sequentially, starting at 1, wrapping
  at 65535. `SessionId = 0` is reserved for pre-session packets (HELLO, ERROR,
  NOT_MODIFIED, AUTH trigger).
- Each session runs in a **dedicated background thread** on the server.
- The server's listen loop runs on a single thread. `AUTH` (which includes a
  synchronous HTTP call to the WebAPI to fetch the frame file) must complete
  within **2 seconds** to avoid blocking HELLO processing for other devices.

### Session states (server side)

```
IDLE ──HELLO──▶ AUTHENTICATING ──frame ok──▶ TRANSFERRING ──DONE──▶ CLOSED
                     │                            │
                     └──error──▶ ERROR sent        └──timeout──▶ ERROR sent
```

---

## 5. Protocol Flows

### 5.1 Normal Frame Delivery

The common path when the device needs a new frame and the server has one ready.

```
Device                              Server
  │                                   │
  │──── HELLO ────────────────────────▶│  Seq = CRC of last frame (0 if none)
  │     hello_payload_t                │  Payload = hello_payload_t (31 bytes)
  │                                   │
  │                                   │  [server fetches frame file via WebAPI]
  │                                   │  [CRC check: client CRC ≠ file CRC → proceed]
  │                                   │
  │◀─── AUTH ─────────────────────────│  SessionId = assigned (≥ 1)
  │     SessionId = N                  │  Seq = total frame file size in bytes
  │     Seq = total_file_size          │
  │                                   │
  │◀─── DATA[seq=0] ──────────────────│  First chunk, up to 1385 bytes
  │──── ACK[seq=0] ───────────────────▶│
  │                                   │
  │◀─── DATA[seq=1] ──────────────────│
  │──── ACK[seq=1] ───────────────────▶│
  │         ...                        │
  │◀─── DATA[seq=N, FLAG_LAST] ───────│  Last chunk, FLAG_LAST set
  │──── ACK[seq=N] ───────────────────▶│
  │                                   │
  │◀─── DONE ─────────────────────────│  Transfer complete
  │                                   │
```

The `Seq` field in AUTH carries the **total file size** so the device can
pre-allocate a receive buffer before the first DATA chunk arrives.

### 5.2 CRC Dedup — Frame Not Modified

When the device already has the current frame, no data is transferred.

```
Device                              Server
  │                                   │
  │──── HELLO ────────────────────────▶│  Seq = stdCRC32 of current frame
  │                                   │
  │                                   │  [server: clientCRC == fileStdCRC → skip]
  │                                   │
  │◀─── NOT_MODIFIED ─────────────────│  No payload. Session ends.
  │                                   │
```

Round-trip cost: ~1 ms (one HELLO + one NOT_MODIFIED, no data transfer).

### 5.3 Delta Delivery

When a pre-computed delta file exists for the device's current CRC, only
the changed regions are sent.

```
Device                              Server
  │                                   │
  │──── HELLO ────────────────────────▶│  Seq = stdCRC32 of current frame
  │                                   │
  │                                   │  [server: CRC ≠ current, checks for
  │                                   │   .delta.{clientCRC}.bin file → found]
  │                                   │
  │◀─── AUTH ─────────────────────────│  Seq = delta file size (not full frame)
  │◀─── DATA[...] ────────────────────│  Delta data chunks
  │──── ACK[...] ─────────────────────▶│
  │◀─── DONE ─────────────────────────│
  │                                   │
```

Delta files are named: `{guid}.delta.{baseCRC_HEX8}.bin`
where `baseCRC_HEX8` is the 8-character hex of the CRC the device currently has.

### 5.4 Class-C Batch Transfer

Delivers multiple animation frames to the device's flash partition for
autonomous looped playback (Class-C templates).

```
Device                              Server
  │                                   │
  │──── HELLO ────────────────────────▶│
  │                                   │
  │◀─── AUTH ─────────────────────────│  Seq = total batch size in bytes
  │                                   │
  │◀─── BATCH_START ──────────────────│  Payload = batch_start_payload_t
  │                                   │  (frame_count, fps, frame_sizes[])
  │──── ACK ──────────────────────────▶│
  │                                   │
  │◀─── DATA[seq=0] ──────────────────│  Frame 0 first chunk
  │──── ACK[seq=0] ───────────────────▶│
  │        ...                         │  All frames transferred as
  │◀─── DATA[seq=N, FLAG_LAST] ───────│  one contiguous byte stream.
  │──── ACK[seq=N] ───────────────────▶│  Device splits into frames using
  │                                   │  frame_sizes[] from BATCH_START.
  │◀─── BATCH_COMMIT ─────────────────│  All frames stored. Begin playback.
  │                                   │
```

After BATCH_COMMIT the device:
1. Writes all frames to its flash partition.
2. Enters autonomous playback loop (no server contact needed).
3. Continues sending PING heartbeats while playing.
4. If a new HELLO cycle starts (server push), stops playback and re-enters transfer mode.

### 5.5 Server-Initiated Refresh (Push)

The server can instruct an idle device to fetch a new frame immediately,
without waiting for the device's next HELLO cycle.

```
Device                              Server
  │                                   │
  │                                   │  [PushLoop detects new frame ready]
  │                                   │
  │◀─── AUTH (trigger) ───────────────│  SessionId = 0, no payload.
  │                                   │  Device treats this as "new frame available".
  │                                   │
  │──── HELLO ────────────────────────▶│  Device immediately sends HELLO
  │                                   │  [normal transfer follows]
```

The trigger AUTH packet has `SessionId = 0` and no payload. This is the only
packet where AUTH is sent by the server without a preceding HELLO.

### 5.6 Device Heartbeat (PING)

Devices send PING at a configurable interval (default: 30 seconds) to signal
liveness and report diagnostics.

```
Device                              Server
  │                                   │
  │──── PING ─────────────────────────▶│  Payload = ping_payload_t
  │                                   │  (uptime, free_heap, wifi_rssi)
  │◀─── ACK ──────────────────────────│  SessionId echoed from PING header
  │                                   │
```

The server records the PING event in the database and notifies the dashboard
via SignalR (`DevicePing` event).

### 5.7 Server Config Push

The server can update device operating parameters at any time.

```
Device                              Server
  │                                   │
  │◀─── CONFIG ───────────────────────│  Payload = config_payload_t
  │                                   │  Optional: FLAG_RESTART set
  │──── ACK ──────────────────────────▶│
  │                                   │
  │  [if FLAG_RESTART: esp_restart()]  │
  │                                   │
```

### 5.8 Error Handling

The server rejects invalid HELLO requests with an ERROR packet.

```
Device                              Server
  │                                   │
  │──── HELLO ────────────────────────▶│  (bad token / unknown device / no template)
  │                                   │
  │◀─── ERROR ────────────────────────│  Payload = 1 byte error code (see §9)
  │                                   │
```

#### ACK Timeout and Retransmission

If the server does not receive ACK within **5 seconds**, it retransmits the
DATA chunk with `FLAG_RETRANSMIT` set. After **3 consecutive failures** on the
same chunk, the server sends `ERROR` with `FLAG_TIMEOUT` and terminates the session.

```
Device                              Server
  │                                   │
  │◀─── DATA[seq=N] ──────────────────│
  │  (ACK lost or device unreachable) │
  │                                   │  [timeout 5s]
  │◀─── DATA[seq=N, RETRANSMIT] ──────│  Retry 1
  │                                   │  [timeout 5s]
  │◀─── DATA[seq=N, RETRANSMIT] ──────│  Retry 2
  │                                   │  [timeout 5s]
  │◀─── ERROR[TIMEOUT] ───────────────│  Session terminated
  │                                   │
```

---

## 6. CRC Algorithm

Two CRC32 variants are used for different purposes. Both use the standard
IEEE 802.3 polynomial (`0xEDB88320` reflected).

| Name | Init value | Final XOR | Used for |
|------|-----------|-----------|----------|
| `fileCrc` | `0x00000000` | none | Delta file naming (`baseCRC` field in delta header) |
| `fileStdCrc` | `0xFFFFFFFF` | `0xFFFFFFFF` | CRC dedup comparison with device HELLO `Seq` |

The ESP32 computes its frame CRC using `esp_rom_crc32_le(0, data, len)`, which
is equivalent to `fileStdCrc` (init=`0xFFFFFFFF`, finalXOR=`0xFFFFFFFF`).

**Do not swap the two variants.** Delta matching uses `fileCrc`; dedup comparison
uses `fileStdCrc`. Using the wrong variant causes all transfers to appear as
cache misses or all deltas to be ignored.

---

## 7. Frame File Formats

Frame files are stored in `ExchangeFolder/` as `{guid}.bin`. The first byte
of the file identifies the encoding format.

| Flag byte | Name | Description |
|-----------|------|-------------|
| `0x00` | Raw BGR565 | Uncompressed. 480×320×2 = 307 200 bytes. Fallback when palette > 255 colors. |
| `0x01` | Palette + RLE | Browser push path only. Run-length encoded with color palette. |
| `0x03` | Palette + LZ4 | **Server default.** Color palette followed by LZ4-compressed indices. Typical size: 20–120 KB. |
| `0x04` | Delta | Changed rectangular regions only. See delta layout below. |

### Delta File Layout (flag `0x04`)

```
 Offset  Size  Field
      0     1  flag = 0x04
      1     4  base_crc   (uint32 LE) — CRC of frame the device currently has
      5     4  new_crc    (uint32 LE) — CRC of frame after applying this delta
      9     2  region_count (uint16 LE)
     11     ▼  regions[]:
              2  x       (uint16 LE) — left edge of changed region in pixels
              2  y       (uint16 LE) — top edge
              2  width   (uint16 LE)
              2  height  (uint16 LE)
              N  pixels  — width × height × 2 bytes, BGR565 big-endian
```

Delta files are named: `{guid}.delta.{baseCRC_HEX8}.bin`

Multiple delta files can coexist for the same base `.bin`, one per distinct
"previous CRC" value. This allows devices that missed one or more grab cycles
to still benefit from delta transfer.

---

## 8. Timing and Retransmission

### Stop-and-wait throughput model

The current implementation uses stop-and-wait: the server sends one chunk
and blocks until ACK before sending the next.

```
Effective throughput = chunk_size / RTT
                     = 1385 bytes / RTT_seconds
```

| Wi-Fi RTT | Throughput |
|-----------|-----------|
| 20 ms | ~69 KB/s |
| 40 ms | ~35 KB/s |
| 80 ms | ~17 KB/s |

### Transfer time by frame type

| Frame type | Typical size | RTT 20 ms | RTT 40 ms |
|------------|-------------|-----------|-----------|
| NOT_MODIFIED | 0 B | ~1 ms | ~1 ms |
| Delta (10% changed) | 8–20 KB | ~0.1–0.3 s | ~0.2–0.6 s |
| Palette + LZ4 | 40–80 KB | ~0.6–1.2 s | ~1.2–2.3 s |
| Raw BGR565 | 307 KB | ~4.5 s | ~9 s |

### Timeout parameters

| Parameter | Value | Configurable |
|-----------|-------|-------------|
| ACK wait timeout | 5 000 ms | No (compile-time) |
| Max retries per chunk | 3 | No (compile-time) |
| PING interval (default) | 30 s | Yes (CONFIG packet) |
| AUTH HTTP fetch timeout | 2 000 ms | No |

### Future: Sliding window

Sliding window (W chunks in-flight simultaneously) would multiply throughput
by W. The protocol already carries per-chunk `Seq` and `FLAG_RETRANSMIT`, so
the signalling infrastructure is in place. Not yet implemented.

---

## 9. Error Codes

Carried as a single byte in the `ERROR` packet payload.

| Code | Name | Description |
|------|------|-------------|
| `0x01` | `BAD_TOKEN` | HELLO payload could not be parsed as a valid GUID. |
| `0x02` | `UNKNOWN_DEVICE` | Token not found in the device database. |
| `0x03` | `NO_TEMPLATE` | Device exists but has no active template assigned. |
| `0x04` | `NO_FRAME` | Template assigned but no successful grab has completed yet. |

An ERROR with no payload (PayloadLength = 0) indicates a generic server-side
failure (e.g., file I/O error).

---

## 10. Implementation Notes

### Cross-language binary compatibility

The `packet_header_t` struct (C) and `PacketHeader` struct (C#) must remain
**binary-identical**. Both are declared with `Pack=1` / `#pragma pack(1)`.
Any change to field order, size, or added fields requires simultaneous updates
in both `packet.h` (firmware) and `UDPUtils.cs` (server).

The same applies to all payload structs: `hello_payload_t`, `ping_payload_t`,
`config_payload_t`, `batch_start_payload_t`.

### Magic token filtering

Every incoming packet is checked against `Magic = 0xABADBABE` before any
further parsing. Packets with wrong magic are silently discarded. This
filters broadcast traffic, mDNS, and other devices on the same network
segment sharing port 11000.

### Session routing

The server's single receive socket dispatches incoming packets by type:

- `HELLO` → `Auth()` call on the listen thread (synchronous, ≤2 s)
- `PING` → async fire-and-forget (non-blocking on listen thread)
- `ACK` → forwarded to the matching session thread via `AutoResetEvent`

Never add blocking I/O to the `HELLO` / `ACK` dispatch path.

### Frame cache

The server caches frame file metadata in memory for 5 minutes (success)
or 30 seconds (failure). This avoids an HTTP loopback call to the WebAPI
on every HELLO when the frame has not changed.

### Security considerations

The protocol provides no encryption or replay protection. It is designed
for trusted local networks (home Wi-Fi, LAN). The magic token and device
GUID token provide basic filtering but not authentication against a
determined adversary. Do not expose port 11000 directly to the public internet.
