# Internal Pipelines Reference

This document describes the internal data flows — how components communicate, how a template goes from HTML to pixels on a display, and how the system self-heals when things go wrong.

---

## Content delivery pipeline (end-to-end)

```
User action (or timer)
        │
        ▼
┌───────────────────┐
│   GrabChannel     │  In-memory async queue
│   (enqueue)       │  ScreenShotParamDTO: TokenGuid, TabName, Width, Height,
└───────┬───────────┘  GrabLogId, TemplateId, UserId, CloseTabAfterCapture
        │
        ▼
┌───────────────────┐
│   GrabWorker      │  Background consumer of GrabChannel
│   Chrome.cs       │  1. AddUrlAsync → open tab, navigate, wait networkidle0
│                   │  2. SetViewportAsync(w, h) + ReloadAsync
│                   │  3. ScreenshotDataAsync → PNG bytes
│                   │  4. Image.Load → Resize → IPixelConverter.Convert → byte[]
│                   │  5. CRC check → skip if unchanged
│                   │  6. WriteAllBytesAsync(.tmp) → File.Move(.bin)  [atomic]
│                   │  7. SaveAsPng(.png)
│                   │  8. GrabLog.MarkCompleted
│                   │  9. IFrameRefreshQueue.Signal(deviceToken)
└───────┬───────────┘
        │
        ▼
ExchangeFolder/{TokenGuid}.bin
        │
        ├──────────────────────────────────────────────────────────────┐
        │ (polling / push)                                             │
        ▼                                                              │
┌───────────────────┐                                       ┌─────────────────┐
│  IFrameRefreshQueue│                                       │  UDP Server     │
│  (signal)         │──► PushLoop (every 500 ms)            │  PushLoop polls │
│                   │    → ConsumeRestart(token)             │  /api/internal/ │
│                   │    → SendTo(device, AUTH_TRIGGER)      │  device/{t}/    │
└───────────────────┘                                       │  frame          │
                                                            └──────┬──────────┘
                                                                   │
                              ┌────────────────────────────────────┘
                              │
                              ▼
                     Device receives AUTH_TRIGGER or sends HELLO
                              │
                              ▼
                     UDPServerSession spawned
                              │
                     ┌────────┴──────────┐
                     │  Single frame     │  Class A/B
                     │  AUTH → DATA×N    │  → DONE
                     │                   │  → Device renders to LCD
                     └───────────────────┘
                     ┌────────┴──────────┐
                     │  Batch (Class C)  │  BATCH_START → N×DATA
                     │                   │  → BATCH_COMMIT
                     │                   │  → Device writes flash partition
                     │                   │  → Player task starts animation
                     └───────────────────┘
```

---

## GrabChannel

**Type:** In-memory async queue (`System.Threading.Channels.Channel<ScreenShotParamDTO>`)  
**Producers:** `TemplatesController.Grab`, `DeviceTemplateRefreshWorker`, `GuestDeviceController.TriggerImmediateGrabAsync`, `DevicesController.CreateStatusTemplate`  
**Consumer:** `GrabWorker` (single consumer, sequential processing)

**ScreenShotParamDTO fields:**

| Field | Type | Notes |
|---|---|---|
| `TokenGuid` | Guid | Output file token: `{ExchangeFolder}/{TokenGuid}.bin` |
| `TabName` | string | Chrome tab identifier returned by `AddUrlAsync` |
| `Width` | int | Target device width |
| `Height` | int | Target device height |
| `GrabLogId` | int | FK to `GrabLogs` — marked complete after screenshot |
| `TemplateId` | int | For GrabLog and CRC cache key |
| `UserId` | int | For resource ownership checks |
| `CloseTabAfterCapture` | bool | `true` for ephemeral grabs; `false` for KeepTabAlive |
| `Monochrome` | bool | Inject `filter:grayscale(1)` before screenshot |
| `ConverterId` | string? | Which `IPixelConverter` to use; default = BGR565+RLE |

**Why a channel?** Chromium is single-threaded for screenshot purposes — concurrent `ScreenshotDataAsync` calls on the same browser produce flaky results. The channel enforces sequential capture while allowing producers to enqueue without blocking.

---

## IFrameRefreshQueue

**Interface:** `IFrameRefreshQueue`  
**Implementation:** In-memory `ConcurrentDictionary<string, bool>` (token → needsRefresh flag)

After Chrome writes a new `.bin`, it calls:
```csharp
_frameRefreshQueue.Signal(deviceToken);
```

The UDP Server's `PushLoop` (polling every 500ms) calls:
```csharp
var result = await _webApiClient.ConsumeRestartAsync(token);
bool shouldRefresh = result.HasPendingFrameRefresh;
```

`POST /api/internal/device/{token}/consume-restart` atomically reads and clears the flag via `IFrameRefreshQueue.ConsumeRefresh(device.Id)` inside the controller — only the first `PushLoop` iteration sees `true`. This prevents multiple `AUTH_TRIGGER` packets for the same frame update.

**Effect:** When a new frame is available, the device receives an unsolicited `AUTH_TRIGGER` packet within ~500ms. Without this, the device would only pick up the new frame on its next scheduled HELLO (up to `BATCH_IDLE_S = 300` seconds later).

---

## Frame resolution chain

How the UDP Server turns a device token into a file path:

```
Device HELLO: token = "57ca9af66f3840059521009e340141e2"
        │
        ▼
GET /api/internal/device/{token}/frame
        │
        ├─ Security check: RemoteIP must be 127.0.0.1
        ├─ Device.GetByTokenAsync(token)
        ├─ device.ActiveTemplateId → null → ERROR(ERR_NO_TEMPLATE)
        │
        ▼
GrabLog.GetLastSuccessfulAsync(templateId, device.DisplayWidth, device.DisplayHeight)
        │
        ├─ null → ERROR(ERR_NO_FRAME)  ← first grab not yet complete
        │
        ▼
FrameInfoDto:
  TokenGuid          = grabLog.TokenGuid
  FilePath           = {ExchangeFolder}/{TokenGuid}.bin
  BatchManifestPath  = {ExchangeFolder}/{TokenGuid}_batch.json  (if exists, Class C)
        │
        ▼
UDPServer.Auth():
  File.Exists(FilePath)?
    No  → ERROR(ERR_NO_FRAME)
    Yes → read BatchManifestPath?
            Yes → BATCH_START → N × DATA → BATCH_COMMIT
            No  → AUTH → DATA × M → DONE
```

**Guest device path** (no `Device` or `Template` DB record):

```
GuestDevice.GetByGuidAsync(guid)
  → GuestDevice.TemplateId → DB template → same chain as above
  → GuestDevice.TemplateName → legacy filesystem template
      FilePath = {ExchangeFolder}/{GuestDevice.Guid}.bin
      (written directly by GuestDeviceController.PushFrame or GuestDeviceRefreshWorker)
```

---

## DeviceTemplateRefreshWorker — scheduling logic

Runs on a 5-minute `PeriodicTimer`. For each iteration:

```
1. Load all templates that have at least one active connection
   (JOIN Connections ON TemplateId, load distinct TemplateIds)

2. For each template:
   a. Parse metadata from HtmlContent:
      vowClass, vowRefresh, vowFrames, vowFps

   b. For each unique (displayWidth, displayHeight) among connected devices:

      c. Check skip conditions:
         - vowRefresh == 0 AND grabLog exists → skip
         - vowClass == C:
             htmlHash = SHA256(HtmlContent)
             sidecarPath = {ExchangeFolder}/t{id}_{w}x{h}.htmlhash
             manifestExists = GrabLog.GetLastSuccessfulAsync(id, w, h) != null
             if File.ReadAllText(sidecarPath) == htmlHash AND manifestExists → skip

      d. Grab:
         - Class C: GrabMultiframeAtResolutionAsync(template, w, h, frames, ct)
             for N in [0..frames-1]:
               url = "t{id}.html?vow_frame={N}"
               Chrome grab → {tmpGuid}_f{N}.bin
             compute BatchCRC = CRC32 over all frame bytes
             write {grabGuid}_batch.json:
               { "frames": N, "fps": fps, "batchCrc": CRC }
             write sidecar: {ExchangeFolder}/t{id}_{w}x{h}.htmlhash = htmlHash
         - Class A/B: GrabAtResolutionAsync(template, w, h, ct)
             Chrome grab → {grabGuid}.bin

      e. PruneStaleGrabsAsync(templateId, w, h, keep=4)
         Delete GrabLog entries and their .bin/.png files beyond the 4 most recent
```

---

## UDPServerSession — transfer state machine

Each `UDPServerSession` runs on its own background thread. State machine per transfer:

```
IDLE
  │ Auth() called
  ▼
AUTH_SENT (single frame) ──────────────────────────────────────────────────►
  │                                                                          │
  │ Seq=N DATA received                                                      │
  ▼                                                                          │
DATA_LOOP                                                                    │
  for each chunk:                                                            │
    send DATA packet (payload = up to 1385 bytes)                           │
    wait for ACK (seq must match)                                           │
    on timeout: retransmit (up to RETRANSMIT_MAX times)                    │
    on FLAG_LAST: set lastChunk=true                                        │
  │                                                                          │
  ▼ (FLAG_LAST)                                                             │
DONE_SENT                                                                    │
  send DONE packet                                                           │
  wait for ACK                                                               │
  on timeout: retransmit DONE (up to ACK_RETRY_MAX=3)                      │
  │                                                                          │
  ▼                                                                          │
SESSION_COMPLETE ◄──────────────────────────────────────────────────────────┘
  PipelineEvent: udp_frame_sent
  Done event fired → dashboard update
  Session removed from _sessions dict

BATCH_START ──►  frame 0 DATA_LOOP ──► BATCH_COMMIT (after last frame) ──► SESSION_COMPLETE
```

**Timeout and retransmit policy:**
- `SO_RCVTIMEO` on the send socket: 3 seconds per chunk wait
- On ACK timeout: retransmit same DATA chunk, increment retransmit counter
- After `RETRANSMIT_MAX` retransmits without ACK: abort session, device will retry on next HELLO

---

## Pipeline event logging

Every significant state transition logs a `PipelineEvent`:

| Where | Event logged |
|---|---|
| `GrabChannel.EnqueueAsync` | `grab_queued` |
| `Chrome.TakeScreenshotAsync` (success) | `grab_completed` |
| `Chrome.TakeScreenshotAsync` (failure) | `grab_failed` |
| `UDPServer.Auth()` (accepted) | `udp_session_opened` |
| `UDPServer.Auth()` (NOT_MODIFIED) | `udp_not_modified` |
| `UDPServerSession` (DONE sent) | `udp_frame_sent` |
| `POST /api/internal/device-heartbeat` (device was offline) | `device_online` |
| `DeviceMonitorService` (timeout) | `device_offline` |

The `PipelineView` React component queries these events and renders them as a multilane timeline — useful for diagnosing where in the pipeline a delivery stalled.

---

## SignalR hub event flow

```
User creates template (POST /api/templates)
  → TemplatesController
  → ITemplateRepository.CreateAsync
  → IDashboardNotificationService.NotifyTemplateCreated(userId, templateDto)
  → Hub: group "user-{userId}" receives TemplateCreated event
  → FlowCanvas.jsx: add TemplateNode to canvas
```

```
New grab completes (Chrome.cs)
  → GrabLog.MarkCompleted
  → IDashboardNotificationService.NotifyGrabCompleted(userId, grabLogDto)
  → Hub: group "user-{userId}" receives GrabCompleted event
  → TemplateNode: clear "grabbing" spinner, refresh preview thumbnail URL
```

```
Device sends HELLO (UDP Server)
  → POST /api/internal/hello/{token}
  → Device.UpdateHardwareStats(rssi, heap, uptime, firmwareVersion)
  → IDashboardNotificationService.NotifyDeviceStatusChanged(userId, deviceStatusDto)
  → Hub: group "user-{userId}" receives DeviceStatusChanged event
  → DeviceNode: update status dot and live stats display
```

---

## Anti-patterns and known limitations

**Sequential Chrome grabs:** The `GrabChannel` consumer is single-threaded. If 3 templates need grabbing, they queue up. A 3-second grab + 45-second Class C batch blocks all other grabs. Mitigation: Class C skip-on-unchanged-HTML prevents most unnecessary batch grabs.

**In-memory `IFrameRefreshQueue`:** If the Grabber API restarts, all pending refresh signals are lost. Devices pick up the next frame on their next HELLO within `BATCH_IDLE_S` seconds (max 5 minutes). Acceptable for the current scale; a persistent queue would be needed for sub-minute delivery guarantees after server restart.

**SQLite write serialisation:** EF Core with SQLite serialises all writes through a single connection. Under concurrent request load (e.g., many devices pinging simultaneously), write latency can spike. The current deployment serves < 20 devices; this becomes a constraint above ~100 concurrent devices.

**`_deviceBatchCrc` dictionary:** The NOT_MODIFIED CRC cache is in-memory in `UDPServer.cs`. A server restart clears it. The manifest-based fallback (`manifest.BatchCrc`) recovers within one HELLO cycle. The NVS-persisted CRC on the device side means this causes at most one extra BATCH download per restart.
