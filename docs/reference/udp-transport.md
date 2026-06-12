# UDP Transport

The UDP transport layer is one of the most battle-tested parts of ViewOwl. Keeping an ESP32 alive and responsive on a WiFi network for days or weeks — without hangs, without WDT reboots, without silent data corruption — is genuinely hard. This document describes the full protocol, every design decision, and the edge cases that required each one.

> **Reuse note:** The protocol described here is completely independent of the rendering pipeline. If you need a reliable, high-throughput UDP file-transfer protocol for any ESP32 project, the design and the firmware client can be lifted as-is.

---

## Why UDP, not TCP?

TCP would seem the obvious choice for reliable file delivery. In practice, on embedded WiFi:

- **TCP on lwIP/ESP-IDF has known stall bugs** under sustained load — particularly with ACK coalescing and the Nagle algorithm. What looks like a working connection silently stops progressing.
- **TCP recovery from a dropped connection is slow** — the reconnect-and-retransmit cycle on lwIP is slow enough to cause visible display freezes.
- **UDP gives us full control.** We know exactly which chunk was lost, we retransmit only that chunk, and we move on. No kernel buffering surprises.

The cost is that we implement our own ACK protocol. That cost is worth it.

---

## Packet structure

Defined in C# at [`ViewOwl.UDP.Utils/UDPUtils.cs`](../../ViewOwl.UDP.Utils/UDPUtils.cs) and mirrored in C at [`ViewOwl.ESP32.Client/main/packet.h`](../../ViewOwl.ESP32.Client/main/packet.h). **These two files must stay in sync.**

```
PacketHeader — 15 bytes, packed (no padding):

  Offset  Size  Field           Notes
  ──────  ────  ─────────────   ────────────────────────────────────────────
  0       4     Magic           0xABADBABE — drops unrelated UDP traffic
  4       1     PacketType      HELLO/AUTH/DATA/ACK/DONE/ERROR/PING/NOT_MODIFIED
  5       2     SessionId       Assigned at AUTH; must match on every subsequent packet
  7       4     Seq             Chunk index (DATA) or frame CRC (HELLO)
  11      2     PayloadLength   Bytes of payload following the header
  13      2     Flags           LAST | RETRANSMIT | TIMEOUT | BADPACKET | BYE
```

Total packet size: **1400 bytes** (header + payload). Fits within the practical WiFi MTU without IP fragmentation.

### `PacketHelper` — factory methods (C#)

`PacketHelper` in `UDPUtils.cs` constructs outgoing packets as `byte[]`. All methods write the 15-byte header plus any payload into a fresh array.

| Method | Packet type | Notes |
|---|---|---|
| `CreateErrorPacket(sessionId, errorCode)` | `ERROR` | Single-byte payload = error code |
| `CreatePingAck(sessionId, seq)` | `ACK` | Acknowledges a PING from the device |
| `CreateConfigPacket(sessionId, pingIntervalS, restart)` | `CONFIG` | Payload = `ConfigPacketPayload`; sets `FLAG_RESTART` when `restart=true` |
| `CreateNotModifiedPacket(sessionId)` | `NOT_MODIFIED` | No payload; tells device to skip download |
| `CreateBatchStartPacket(sessionId, payload)` | `BATCH_START` | Payload = `BatchStartPayload` (68 bytes) |
| `CreateBatchCommitPacket(sessionId)` | `BATCH_COMMIT` | No payload; device starts looped playback |
| `CreateAuthTriggerPacket()` | `AUTH` | Server-initiated push; sessionId = 0, no payload |

---

## Protocol state machine

### Normal transfer (new frame)

```
Device (ESP32)                         Server (UDPServer.cs)
──────────────                         ─────────────────────

HELLO  ──────────────────────────────►  ListenLoop receives
       token (16 bytes) +               Auth() called:
       displayWidth + displayHeight +     • lookup device in DB
       current_frame_crc                  • fetch .bin from ExchangeFolder
                                          • CRC match? → NOT_MODIFIED
                                          • else → send AUTH

       ◄────────────────────────────── AUTH
                                         sessionId + totalFrameSize

for each 1385-byte chunk:
  wait for DATA ◄──────────────────── DATA  seq=N, payload=chunk
  send ACK ──────────────────────────► ACK  seq=N
  (write chunk to DRAM or flash)

                                       On FLAG_LAST set:
       ◄────────────────────────────── DONE
ACK ───────────────────────────────►

render frame to LCD
enter idle (PING loop)
```

### NOT_MODIFIED (frame unchanged)

```
Device                                 Server

HELLO + crc=0xAABBCCDD ──────────────► CRC matches stored batch CRC
                                        (in _deviceBatchCrc dict or manifest)
       ◄────────────────────────────── NOT_MODIFIED

skip download, continue playback
```

### Batch transfer (Class C animation)

```
Device                                 Server

HELLO  ──────────────────────────────► reads {guid}_batch.json manifest
                                        sends BATCH_START (frame count + fps)

       ◄────────────────────────────── BATCH_START

for each frame [0..N-1]:
  receive frame data (same DATA/ACK loop as single frame)

       ◄────────────────────────────── BATCH_COMMIT

write all frames to flash partition atomically
save batch CRC to NVS
give player semaphore → animation starts
enter long idle (BATCH_IDLE_S = 5 min)
```

---

## Server side — `UDPServer.cs`

**Language:** C# 12, .NET 8  
**File:** [`ViewOwl.UDP.Server/UDPServer.cs`](../../ViewOwl.UDP.Server/UDPServer.cs)

### Architecture

Single-socket design: one `Socket.ReceiveFrom()` loop reads all incoming packets. Each authorised device gets its own `UDPServerSession` on a background thread for sending. This avoids concurrent reads on the same socket (which UDP does not support cleanly) while allowing multiple devices to receive frames in parallel.

### Key fields

| Field | Type | Purpose |
|---|---|---|
| `_socket` | `Socket` | Single UDP socket bound to port 11000 |
| `_sessions` | `ConcurrentDictionary<IPEndPoint, UDPServerSession>` | Active per-device send threads |
| `_knownDevices` | `ConcurrentDictionary<IPEndPoint, string>` | Endpoint → token map; persists between sessions for PING routing |
| `_deviceBatchCrc` | `ConcurrentDictionary<string, uint>` | Last committed batch CRC per device; enables NOT_MODIFIED without reading the manifest |
| `_frameCache` | `ConcurrentDictionary<string, (FrameResult, DateTime)>` | 30-second TTL cache of frame lookups; avoids loopback HTTP round-trip on every HELLO |

### Threads

- **`ListenLoop`** — synchronous `ReceiveFrom()` with 1-second timeout. Dispatches HELLO to `Auth()`, routes PING to `HandlePing()`, forwards DATA/ACK/DONE to the appropriate `UDPServerSession`.
- **`PushLoop`** — polls every 500 ms; sends an AUTH trigger packet to idle devices when a new frame is ready (server-initiated push without waiting for the device's next HELLO).

### `Auth()` decision tree

```
HELLO received
  │
  ├─ Token invalid format           → ERROR(ERR_BAD_TOKEN)
  ├─ Token not in DB                → ERROR(ERR_UNKNOWN_DEVICE)
  ├─ Device has no template         → ERROR(ERR_NO_TEMPLATE)
  ├─ .bin not found on disk         → ERROR(ERR_NO_FRAME)
  ├─ _deviceBatchCrc[token] == clientCRC     → NOT_MODIFIED  (in-memory fast path)
  ├─ manifest.BatchCrc != 0 && == clientCRC  → NOT_MODIFIED  (manifest fallback)
  ├─ manifest exists                → BATCH_START → N × DATA → BATCH_COMMIT
  └─ single frame                   → AUTH → DATA → DONE
```

### Socket options

```csharp
// DontFragment: we size packets to fit MTU — fragmentation is a bug, not a fallback
_socket.SetSocketOption(IP, DontFragment, true);

// 1-second receive timeout: lets ListenLoop check cancellationToken regularly
_socket.SetSocketOption(Socket, ReceiveTimeout, 1000);

// Minimal buffers: we don't queue — each packet is sent and confirmed before the next
_socket.SetSocketOption(Socket, SendBuffer,    PacketSize);
_socket.SetSocketOption(Socket, ReceiveBuffer, PacketSize);

// Windows only: suppress ICMP Port Unreachable on send to offline device
_socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
```

---

## Client side — `udp_client.c`

**Language:** C99, ESP-IDF 5.5  
**File:** [`ViewOwl.ESP32.Client/main/udp_client.c`](../../ViewOwl.ESP32.Client/main/udp_client.c)

### The historical problem

Before ESP-IDF 5.x, keeping an ESP32 alive on WiFi for more than a few hours was genuinely difficult. Common failure modes:

- **Socket silently goes dead** — no error returned, `recvfrom()` blocks forever, WDT fires
- **lwIP heap fragmentation** — extended operation fragments the heap until allocations fail
- **WiFi stack hangs** — the supplicant deadlocks on a rare association error, never recovers

ViewOwl's firmware was written to defend against all three.

### Socket resurrection

If `HELLO_FAIL_MAX` (15) consecutive `recvfrom()` calls return no response (each with a 3-second timeout = 45 seconds total), the firmware closes the socket and creates a fresh one:

```c
close(sock);
sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_IP);
// re-apply SO_RCVTIMEO / SO_SNDTIMEO
hello_fail_count = 0;
```

This recovers from a silently dead socket without a full reboot. If `socket()` itself fails, the firmware reboots via `esp_restart()` — the only correct response to a failed socket allocation.

### Watchdog feeding strategy

The firmware runs a 10-minute hardware WDT. Any successful network exchange (including `NOT_MODIFIED`, `ERR_NO_FRAME`, `ERR_NO_TEMPLATE`) resets the WDT timer:

```c
/* Any response from the server — even an error — means the loop is alive */
hello_fail_count = 0;
last_success_for_WDT = esp_timer_get_time();
```

This is intentional: a device that is successfully communicating with a server that has no content for it is still a healthy device. Only genuine silence (no response at all for 75 seconds) indicates a network problem.

### CRC persistence across reboots (NVS)

After a successful BATCH_COMMIT, the CRC of the received batch is written to NVS (non-volatile storage):

```c
nvs_storage_write_batch_crc(batch_data_crc);
```

On the next boot, before the first HELLO:

```c
if (flash_frames_valid()) {
    uint32_t saved_crc = 0;
    if (nvs_storage_read_batch_crc(&saved_crc) == ESP_OK && saved_crc != 0) {
        current_frame_crc = saved_crc;
    }
}
```

Result: the very first HELLO after a reboot carries the real CRC. The server replies `NOT_MODIFIED` and the animation resumes instantly — no 27-second re-download freeze.

The guard `flash_frames_valid()` is critical: if the flash partition was erased (USB reflash), the NVS CRC would be stale. Sending a stale CRC would cause the server to reply `NOT_MODIFIED` while the device has nothing to play. The guard catches this case and forces `CRC=0` → `BATCH` download.

### Idle management

```
after_batch_commit == true   → idle for BATCH_IDLE_S (300 s)
                               animation plays from flash independently
batch_failed == true         → idle for BATCH_RETRY_DELAY_S (30 s)
                               prevents 1-second HELLO hammer on server overload
default                      → idle for IDLE_DURATION_S (1 s)
```

The server's `PushLoop` can break any idle early by sending an AUTH trigger packet. The firmware wakes immediately and sends HELLO. This gives near-instant updates when a new template is grabbed, without the device needing to poll constantly.

### ACK retry on last packet

The most common failure point in a UDP file transfer is the final ACK — the device sends ACK for the last DATA chunk, but the server's DONE packet gets lost, so the server retransmits. The firmware handles this:

```c
// After FLAG_LAST: wait for DONE, retry ACK up to ACK_RETRY_MAX (3) times
// Each attempt waits one full SO_RCVTIMEO period (3 s)
// Maximum stall before aborting: 3 × 3 s = 9 s
```

### `not_modified_no_batch_count` — self-healing for stalled Class C

If a device is in single-frame mode (no batch committed) and receives `NOT_MODIFIED` responses for `BATCH_IDLE_S` consecutive cycles (~5 minutes), it resets its CRC to 0. This forces the server to re-evaluate on the next HELLO and — if a Class C batch has been rendered in the meantime — respond with `BATCH_START` instead of `NOT_MODIFIED`. Without this mechanism a device could be stuck in single-frame mode indefinitely after a server restart.

---

## CRC32 algorithm

Both sides use the same CRC32: **init=0, polynomial=0xEDB88320, no finalXOR**. This matches `esp_rom_crc32_le(0, data, len)` on the firmware side and is implemented manually on the server side in `UDPServerSession.ComputeCrc32OverFrames()`.

If the two implementations diverge, NOT_MODIFIED never matches and devices re-download on every HELLO. This was a real bug (fixed 2026-04-06).

---

## Stability record

In production use on a Linux ARM64 server with multiple ESP32 devices:

- Devices run continuously for weeks without WDT reboots
- Server restarts (e.g. a redeploy) cause at most one delayed HELLO cycle (device CRC persisted in NVS matches manifest CRC → `NOT_MODIFIED` on reconnect)
- Network outages of up to ~75 seconds recover automatically via socket resurrection
- Power cycles recover in under 5 seconds (NVS CRC → immediate NOT_MODIFIED)
