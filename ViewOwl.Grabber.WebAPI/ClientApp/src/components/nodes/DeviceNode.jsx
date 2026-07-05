import { useState, useEffect, useRef, useCallback, memo } from 'react'
import { createPortal } from 'react-dom'
import { Handle, Position } from 'reactflow'
import ControllerConfigModal from '../Controllers/ControllerConfigModal'
import SemiGauge from '../HUD/SemiGauge'
import RadialHistoryHUD from '../HUD/RadialHistoryHUD'
import TransferSpeedHUD from '../HUD/TransferSpeedHUD'
import { authFetch } from '../../utils/auth'
import './DeviceNode.css'

const formatUptime = (seconds) => {
  if (!seconds) return '—'
  const h = Math.floor(seconds / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  return h > 0 ? `${h}h ${m}m` : `${m}m`
}

const formatRelative = (iso) => {
  if (!iso) return '—'
  const mins = Math.floor((Date.now() - new Date(iso).getTime()) / 60000)
  if (mins < 1)  return 'just now'
  if (mins < 60) return `${mins}m ago`
  const hrs = Math.floor(mins / 60)
  if (hrs < 24)  return `${hrs}h ago`
  return `${Math.floor(hrs / 24)}d ago`
}

// Returns the index [0..47] of the current 30-minute slot in the 24h day.
const currentSlotIndex = () => {
  const now = new Date()
  return Math.floor((now.getHours() * 60 + now.getMinutes()) / 30)
}

// Clears SUCCESS/FAIL delivery status after this many ms so the row doesn't linger.
const DELIVERY_CLEAR_MS = 30000

// Initial ping history: 48 null slots (one per 30-minute window).
const EMPTY_HISTORY = Object.freeze(Array(48).fill(null))

const DeviceNode = ({ data }) => {
  const [uptime, setUptime]         = useState(null)   // seconds from last ping
  const [wifiRssi, setRssi]         = useState(() => data.initialWifiRssi ?? null)   // dBm — seeded from DB, updated by live pings
  const [configOpen, setConfigOpen] = useState(false)
  const [delivery, setDelivery]     = useState(null)   // { phase, durationMs, errorMessage, imageSizeBytes, retries }
  // Grabber-lane health for the active template: null = ok/unknown, { error } = latest grab failed.
  // Seeded from the dashboard payload (data.grabOk) so a frozen device reads GRAB FAIL on load,
  // then updated live by GrabberUpdate events routed through FlowCanvas.
  const [grabFail, setGrabFail]     = useState(() =>
    data.grabOk === false ? { error: data.grabError ?? null } : null)
  const [pingHistory, setPingHistory] = useState(() => [...EMPTY_HISTORY])
  const [transferHistory, setTransferHistory] = useState([]) // last 24 completed transfers
  const [initPhase, setInitPhase]   = useState('idle') // idle | loading | done | error
  const clearTimerRef = useRef(null)
  const fileFlagRef   = useRef(null)  // fileFlag carried from TransferStarted → GrabCompleted

  // Records a ping hit (and optional error) into the correct 30-min history slot.
  const recordHistorySlot = useCallback((isError = false) => {
    const slot = currentSlotIndex()
    setPingHistory(prev => {
      const next = [...prev]
      const cur  = next[slot] ?? { count: 0, errorCount: 0 }
      next[slot] = {
        count:      cur.count + 1,
        errorCount: cur.errorCount + (isError ? 1 : 0),
      }
      return next
    })
  }, [])

  // Sync live ping event data into local state and history
  useEffect(() => {
    if (!data.pingEvent) return
    if (data.pingEvent.uptime   != null) setUptime(data.pingEvent.uptime)
    if (data.pingEvent.wifiRssi != null) setRssi(data.pingEvent.wifiRssi)
    recordHistorySlot(false)
  }, [data.pingEvent, recordHistorySlot])

  // Sync delivery event from FlowCanvas
  useEffect(() => {
    if (!data.deliveryEvent) return
    const ev = data.deliveryEvent

    // Remember file flag from TransferStarted so we can attach it to the history entry
    if (ev.phase === 'send' && ev.fileFlag != null) {
      fileFlagRef.current = ev.fileFlag
    }

    setDelivery(ev)

    // Record failed deliveries in the ping history chart
    if (ev.phase === 'fail') {
      recordHistorySlot(true)
    }

    // Append terminal outcomes to the transfer speed history (newest first, cap at 24)
    const isTerminal = ev.phase === 'success' || ev.phase === 'fail' || ev.phase === 'not_modified'
    if (isTerminal) {
      setTransferHistory(prev => [{
        time:      new Date(),
        durationMs: ev.durationMs ?? null,
        sizeBytes:  ev.imageSizeBytes ?? null,
        flag:       fileFlagRef.current,
        retries:    ev.retries ?? 0,
        outcome:    ev.phase === 'not_modified' ? 'not_modified'
                  : ev.phase === 'success'      ? 'success'
                  : 'fail',
      }, ...prev].slice(0, 24))
      if (ev.phase !== 'not_modified') fileFlagRef.current = null
    }

    // Auto-clear terminal states after DELIVERY_CLEAR_MS
    if (clearTimerRef.current) clearTimeout(clearTimerRef.current)
    if (isTerminal) {
      clearTimerRef.current = setTimeout(() => setDelivery(null), DELIVERY_CLEAR_MS)
    }
  }, [data.deliveryEvent, recordHistorySlot])

  // Sync live grabber-lane outcome from FlowCanvas (GrabberUpdate for the active template).
  useEffect(() => {
    if (!data.grabEvent) return
    setGrabFail(data.grabEvent.success ? null : { error: data.grabEvent.errorMessage ?? null })
  }, [data.grabEvent])

  // Clean up timer on unmount
  useEffect(() => () => { if (clearTimerRef.current) clearTimeout(clearTimerRef.current) }, [])

  const handleInitDisplay = useCallback(async () => {
    if (initPhase === 'loading') return
    setInitPhase('loading')
    try {
      const res = await authFetch(`/api/devices/${data.id}/create-status-template`, { method: 'POST' })
      if (res.ok) {
        setInitPhase('done')
        // Reload dashboard after a short delay so the new activeTemplateId arrives
        // and the button is replaced by CONFIG permanently
        setTimeout(() => data.onRefreshDashboard?.(), 2000)
      } else {
        setInitPhase('error')
        setTimeout(() => setInitPhase('idle'), 4000)
      }
    } catch {
      setInitPhase('error')
      setTimeout(() => setInitPhase('idle'), 4000)
    }
  }, [data.id, data.onRefreshDashboard, initPhase])

  const isOnline  = data.status === 'Online'
  const isWarning = isOnline && wifiRssi != null && wifiRssi < -75

  const statusClass = isOnline
    ? (isWarning ? 'device-warning' : 'device-online')
    : 'device-offline'

  const dotClass = isOnline
    ? (isWarning ? 'dot-warning' : 'dot-online')
    : 'dot-offline'

  // Pipeline status — the single "why is this device (not) rendering?" answer, derived
  // from data the node already holds. Colour = meaning (ISA-101). This is the fleet-
  // observability line: a device stuck for days reads its cause at a glance.
  // Grab-lane failure takes precedence over the UDP-delivery verdict: a device can be
  // "delivering" a stale frame (not_modified) while its template's grab is broken — the
  // frozen-content root cause is the grab, so surface it first.
  const pipeline =
    !isOnline                                                  ? { t: 'OFFLINE',           c: 'pipe-off'  }
    : !data.activeTemplateId                                   ? { t: 'NO TEMPLATE',       c: 'pipe-warn' }
    : grabFail                                                 ? { t: 'GRAB FAIL',         c: 'pipe-err'  }
    : delivery?.phase === 'fail'                               ? { t: 'DELIVERY FAIL',     c: 'pipe-err'  }
    : (delivery?.phase === 'success' || delivery?.phase === 'not_modified')
                                                               ? { t: 'RENDERING',         c: 'pipe-ok'   }
    :                                                            { t: 'LINKED · NO FRAME', c: 'pipe-dim'  }

  return (
    <>
      {/* Outer wrapper carries the glow drop-shadow; chamfer lives on the inner. */}
      <div className={`device-node ${statusClass}`}>
        <div className="device-node-inner">

          {/* Header: status dot + name + status badge + delete */}
          <div className="device-header">
            <span className={`status-dot ${dotClass}`} />
            <span className="node-title">{data.name}</span>
            <span className="device-status-badge">{data.status ?? 'Unknown'}</span>
            <button
              className="node-delete-btn"
              onClick={() => data.onDelete?.()}
              onPointerDown={e => e.stopPropagation()}
              title="Delete this controller"
            >✕</button>
          </div>

          {/* Pipeline status — why this device is (not) rendering, at a glance */}
          <div
            className={`device-pipeline ${pipeline.c}`}
            title={grabFail?.error ? `Grab failed: ${grabFail.error}` : undefined}
          >{pipeline.t}</div>

          {/* HUD row: signal gauge (left) + 24h ping history (right) */}
          <div className="device-hud-row">
            <div className="device-hud-half">
              <SemiGauge rssi={wifiRssi} label="SIGNAL" isOnline={isOnline} />
            </div>
            <div className="device-hud-divider" />
            <div className="device-hud-half">
              <RadialHistoryHUD pingHistory={pingHistory} />
            </div>
          </div>

          {/* Transfer speed row: polar throughput chart + history list */}
          <div className="device-hud-row device-speed-row">
            <TransferSpeedHUD transferHistory={transferHistory} />
          </div>

          {/* Body: text stats */}
          <div className="node-body">
            {data.ipAddress && (
              <div className="node-stat">
                <span className="stat-label">IP</span>
                <span className="stat-value">{data.ipAddress}</span>
              </div>
            )}

            {wifiRssi != null && (
              <div className="node-stat">
                <span className="stat-label">RSSI</span>
                <span className="stat-value stat-dim">{wifiRssi} dBm</span>
              </div>
            )}

            {uptime != null && (
              <div className="node-stat">
                <span className="stat-label">UPTIME</span>
                <span className="stat-value stat-primary">{formatUptime(uptime)}</span>
              </div>
            )}

            {data.activeTemplateName && (
              <div className="node-stat">
                <span className="stat-label">TEMPLATE</span>
                <span className="stat-value stat-accent">{data.activeTemplateName}</span>
              </div>
            )}

            {data.displayWidth && data.displayHeight && (
              <div className="node-stat">
                <span className="stat-label">DISPLAY</span>
                <span className="stat-value">{data.displayWidth}×{data.displayHeight}</span>
              </div>
            )}

            <div className="node-stat">
              <span className="stat-label">LAST SEEN</span>
              <span className="stat-value stat-dim">{formatRelative(data.lastSeenAt)}</span>
            </div>
          </div>

          {/* Delivery bar — full-width visual indicator above footer */}
          {delivery && (
            <div className="delivery-bar-wrapper">
              <div className={`delivery-bar-track delivery-bar-${delivery.phase}`} />
              <span className={`delivery-label delivery-${delivery.phase}`}>
                {delivery.phase === 'pending' && 'PENDING'}
                {delivery.phase === 'send'    && 'SENDING'}
                {delivery.phase === 'success'      && `OK ${delivery.durationMs != null ? delivery.durationMs + ' ms' : ''}`}
                {delivery.phase === 'fail'         && `FAIL ${delivery.errorMessage ?? ''}`}
                {delivery.phase === 'not_modified' && 'NOT MODIFIED'}
              </span>
            </div>
          )}

          {/* Footer */}
          <div className="node-footer">
            {/* No template yet — show prominent INIT DISPLAY button */}
            {!data.activeTemplateId && (
              <button
                className={`node-init-btn ${initPhase === 'done' ? 'node-init-done' : ''} ${initPhase === 'error' ? 'node-init-error' : ''}`}
                onClick={handleInitDisplay}
                onPointerDown={e => e.stopPropagation()}
                disabled={initPhase === 'loading'}
                title="Create status template and push first frame to this device"
              >
                {initPhase === 'idle'    && '▶ INIT DISPLAY'}
                {initPhase === 'loading' && 'INITIALIZING...'}
                {initPhase === 'done'    && '✓ INITIATED'}
                {initPhase === 'error'   && '⚠ RETRY'}
              </button>
            )}

            {/* Template assigned — CONFIG + regen button */}
            {data.activeTemplateId && (
              <>
                <button
                  className="node-config-btn"
                  onClick={() => setConfigOpen(true)}
                  onPointerDown={e => e.stopPropagation()}
                >
                  ⚙ CONFIG
                </button>
                <button
                  className={`node-regen-btn ${initPhase === 'done' ? 'node-regen-done' : ''} ${initPhase === 'error' ? 'node-regen-error' : ''}`}
                  onClick={handleInitDisplay}
                  onPointerDown={e => e.stopPropagation()}
                  disabled={initPhase === 'loading'}
                  title="Re-create status template"
                >
                  {initPhase === 'loading' ? '…' : initPhase === 'done' ? '✓' : initPhase === 'error' ? '!' : '↺'}
                </button>
              </>
            )}
          </div>

        </div>{/* /device-node-inner */}

        <Handle type="target" position={Position.Left} id="input" className="handle-input" />
      </div>

      {configOpen && createPortal(
        <ControllerConfigModal
          device={data}
          liveUptime={uptime}
          liveRssi={wifiRssi}
          onClose={() => setConfigOpen(false)}
          onNameSaved={data.onNameSaved}
        />,
        document.body
      )}
    </>
  )
}

export default memo(DeviceNode)
