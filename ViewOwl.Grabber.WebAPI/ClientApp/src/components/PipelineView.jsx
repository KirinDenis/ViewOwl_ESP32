import { Fragment, useState, useEffect, useCallback, useRef, memo } from 'react'
import { authFetch } from '../utils/auth'
import { useSignalR } from '../hooks/useSignalR'
import './PipelineView.css'

// ── Helpers ───────────────────────────────────────────────────────────────

// For events < 60 s old show relative ("12s ago"); for anything older show
// the absolute HH:MM — "2h ago" is useless when the device is supposed to
// update every 5 minutes and you need to know the actual wall-clock time.
function formatEventTime(ts) {
  if (!ts) return '—'
  const date = new Date(ts)
  const diff = (Date.now() - date.getTime()) / 1000
  if (diff < 60) return `${Math.floor(diff)}s ago`
  return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })
}

// Full ISO string for tooltip (title attribute) so the user can see the exact date.
function fullTime(ts) {
  if (!ts) return ''
  return new Date(ts).toLocaleString()
}

function evCls(ev) {
  if (!ev) return 'dim'
  switch (ev.eventType) {
    case 'grab_completed':
    case 'udp_done':
    case 'device_online':      return 'ok'
    case 'grab_failed':
    case 'udp_failed':
    case 'device_offline':     return 'err'
    case 'grab_started':
    case 'udp_session_opened': return 'act'
    case 'udp_not_modified':   return 'dim'
    case 'udp_no_frame':       return 'warn'
    default:                   return 'dim'
  }
}

function devCls(status) {
  switch (status?.toLowerCase()) {
    case 'online':  return 'ok'
    case 'offline': return 'err'
    default:        return 'dim'
  }
}

function shortLabel(t) {
  return (t ?? '—')
    .replace(/^(grab_|udp_|device_)/, '')
    .replace(/_/g, ' ')
    .toUpperCase()
}

// NOISE events — filtered out of the log by default.
const NOISE_TYPES = new Set(['udp_not_modified'])

// Groups consecutive runs of the same eventType into { ...firstEvent, count }.
function groupEvents(events) {
  const result = []
  for (const ev of events) {
    const last = result[result.length - 1]
    if (last && last.eventType === ev.eventType) {
      last._count = (last._count ?? 1) + 1
    } else {
      result.push({ ...ev, _count: 1 })
    }
  }
  return result
}

// Stable merge: keep the same object reference for unchanged rows so React.memo
// can skip re-renders — only rows with new events get a new reference.
function mergeRows(prev, next) {
  if (!prev?.length) return next
  const prevMap = new Map(prev.map(r => [r.templateId, r]))

  return next.map(nr => {
    const or = prevMap.get(nr.templateId)
    if (!or) return nr

    const grabSame = or.lastGrabEvent?.id === nr.lastGrabEvent?.id

    const od = or.devices ?? []
    const nd = nr.devices ?? []
    const devsSame =
      od.length === nd.length &&
      nd.every((d, i) => {
        const p = od[i]
        return (
          p?.id === d.id &&
          p.status === d.status &&
          p.lastUdpEvent?.id    === d.lastUdpEvent?.id &&
          p.lastDeviceEvent?.id === d.lastDeviceEvent?.id
        )
      })

    return grabSame && devsSame ? or : nr
  })
}

// ── Preview image hook ────────────────────────────────────────────────────
// Loads once per templateId; refetches only when the grab event id changes.

function usePreview(templateId, grabEventId) {
  const [url, setUrl]    = useState(null)
  const blobRef          = useRef(null)
  const loadedForRef     = useRef(null)

  useEffect(() => {
    if (!templateId) return
    const key = String(grabEventId ?? 'none')
    if (loadedForRef.current === key && blobRef.current) return
    loadedForRef.current = key

    authFetch(`/api/templates/${templateId}/preview-image`)
      .then(r => (r?.ok ? r.blob() : null))
      .then(blob => {
        if (!blob) return
        if (blobRef.current) URL.revokeObjectURL(blobRef.current)
        const u = URL.createObjectURL(blob)
        blobRef.current = u
        setUrl(u)
      })
      .catch(() => {})
  }, [templateId, grabEventId])

  useEffect(
    () => () => { if (blobRef.current) URL.revokeObjectURL(blobRef.current) },
    []
  )

  return url
}

// ── EventLogDrawer ────────────────────────────────────────────────────────

function EventLogDrawer({ ctx, onClose }) {
  const [events,    setEvents]    = useState([])
  const [loading,   setLoading]   = useState(true)
  const [showNoise, setShowNoise] = useState(false)

  useEffect(() => {
    if (!ctx) return
    setLoading(true)
    setEvents([])
    const p = new URLSearchParams({ limit: 200 })
    if (ctx.templateId) p.set('templateId', ctx.templateId)
    if (ctx.deviceId)   p.set('deviceId',   ctx.deviceId)
    authFetch(`/api/pipeline/events?${p}`)
      .then(r => r?.json())
      .then(d => { if (Array.isArray(d)) setEvents(d) })
      .catch(() => {})
      .finally(() => setLoading(false))
  }, [ctx])

  if (!ctx) return null

  const filtered = showNoise ? events : events.filter(e => !NOISE_TYPES.has(e.eventType))
  const grouped  = groupEvents(filtered)
  const noiseCount = events.filter(e => NOISE_TYPES.has(e.eventType)).length

  return (
    <div className="eld-overlay" onClick={onClose}>
      <div className="eld-drawer" onClick={e => e.stopPropagation()}>

        <div className="eld-header">
          <span className="eld-title">{ctx.label ?? 'Event Log'}</span>
          <button className="eld-close" onClick={onClose}>✕</button>
        </div>

        {/* Filter bar */}
        <div className="eld-filterbar">
          <button
            className={`eld-filter-btn${showNoise ? ' eld-filter-active' : ''}`}
            onClick={() => setShowNoise(v => !v)}
          >
            {showNoise ? '◉' : '○'} not_modified
            {noiseCount > 0 && <span className="eld-noise-count"> ×{noiseCount}</span>}
          </button>
        </div>

        <div className="eld-body">
          {loading && <div className="pl-empty">Loading…</div>}
          {!loading && grouped.length === 0 && (
            <div className="pl-empty">
              {events.length > 0 ? 'All events hidden by filter' : 'No events'}
            </div>
          )}
          {grouped.map((ev, i) => {
            const cls = evCls(ev)
            return (
              <div key={`${ev.id}-${i}`} className="eld-item">
                <span className="eld-time">{new Date(ev.ts).toLocaleTimeString()}</span>
                <span className={`eld-type elt-${ev.eventType}`}>
                  {shortLabel(ev.eventType)}
                  {ev._count > 1 && (
                    <span className="eld-repeat"> ×{ev._count}</span>
                  )}
                </span>
                <span className="eld-right">
                  {ev.durationMs != null && (
                    <span className="eld-dur">{ev.durationMs}ms</span>
                  )}
                  <span className={`eld-dot eld-dot-${cls}`} />
                </span>
                {ev.errorMessage && (
                  <span className="eld-err">{ev.errorMessage}</span>
                )}
              </div>
            )
          })}
        </div>
      </div>
    </div>
  )
}

// ── PipelineRow ────────────────────────────────────────────────────────────

const PipelineRow = memo(function PipelineRow({
  row,
  expanded,
  onToggle,
  onOpenLog,
  onEditTemplate,
  onConfigDevice,
  onGrabNow,
}) {
  const devs       = row.devices ?? []
  const shownDevs  = expanded ? devs : devs.slice(0, 1)
  const rowSpan    = Math.max(shownDevs.length, 1)
  const grabCls    = evCls(row.lastGrabEvent)
  const previewUrl = usePreview(row.templateId, row.lastGrabEvent?.id)

  const toggle = useCallback(() => onToggle(row.templateId), [onToggle, row.templateId])

  return (
    <div className={`pl-row${expanded ? ' pl-row-open' : ''}`}>

      <div className="pl-grid">

        {/* ── Col 1: Template ── */}
        <div
          className="pl-cell pl-col-template"
          style={{ gridRow: `1 / span ${rowSpan}` }}
          onClick={toggle}
        >
          <span className="pl-tpl-name">{row.templateName}</span>
          <span className="pl-tpl-meta">
            {devs.length === 0
              ? 'no devices'
              : `${devs.length} device${devs.length !== 1 ? 's' : ''}`}
            {devs.length > 1 && (
              <span className="pl-caret"> {expanded ? '▲' : '▼'}</span>
            )}
          </span>
          {previewUrl
            ? <img src={previewUrl} className="pl-thumb" alt="" draggable={false} />
            : <div className="pl-thumb-blank" />
          }
        </div>

        {/* ── Col 2: Grabber ── */}
        <div
          className={`pl-cell pl-col-grabber pl-s-${grabCls} pl-clickable`}
          style={{ gridRow: `1 / span ${rowSpan}` }}
          onClick={() => onOpenLog({
            templateId: row.templateId,
            label: `${row.templateName} · Grabber`,
          })}
        >
          {row.lastGrabEvent ? (
            <>
              <span className={`pl-badge pl-b-${grabCls}`}>
                {shortLabel(row.lastGrabEvent.eventType)}
              </span>
              <span className="pl-time" title={fullTime(row.lastGrabEvent.ts)}>
                {formatEventTime(row.lastGrabEvent.ts)}
              </span>
              {row.lastGrabEvent.durationMs != null && (
                <span className="pl-dur">{row.lastGrabEvent.durationMs}ms</span>
              )}
              {row.lastGrabEvent.errorMessage && (
                <span className="pl-errmsg">{row.lastGrabEvent.errorMessage}</span>
              )}
            </>
          ) : (
            <span className="pl-none">no grabs yet</span>
          )}
          {previewUrl && (
            <img src={previewUrl} className="pl-grab-preview" alt="" draggable={false} />
          )}
        </div>

        {/* ── Cols 3+4: one UDP + one Device per visible device ── */}
        {shownDevs.length > 0 ? shownDevs.map(dev => {
          const uCls = evCls(dev.lastUdpEvent)
          const dCls = devCls(dev.status)
          return (
            <Fragment key={dev.id}>

              {/* UDP */}
              <div
                className={`pl-cell pl-col-udp pl-s-${uCls} pl-clickable`}
                onClick={() => onOpenLog({
                  deviceId:   dev.id,
                  templateId: row.templateId,
                  label:      `${dev.name} · UDP`,
                })}
              >
                {dev.lastUdpEvent ? (
                  <>
                    <span className={`pl-badge pl-b-${uCls}`}>
                      {shortLabel(dev.lastUdpEvent.eventType)}
                    </span>
                    <span className="pl-time" title={fullTime(dev.lastUdpEvent.ts)}>
                      {formatEventTime(dev.lastUdpEvent.ts)}
                    </span>
                    {dev.lastUdpEvent.durationMs != null && (
                      <span className="pl-dur">{dev.lastUdpEvent.durationMs}ms</span>
                    )}
                    {dev.lastUdpEvent.errorMessage && (
                      <span className="pl-errmsg">{dev.lastUdpEvent.errorMessage}</span>
                    )}
                  </>
                ) : (
                  <span className="pl-none">—</span>
                )}
              </div>

              {/* Device */}
              <div
                className={`pl-cell pl-col-device pl-s-${dCls} pl-clickable`}
                onClick={() => onOpenLog({ deviceId: dev.id, label: `${dev.name} · Device` })}
              >
                <span className="pl-dev-name">{dev.name}</span>
                <span className={`pl-dev-status pl-ds-${dCls}`}>{dev.status ?? 'unknown'}</span>
                {dev.lastDeviceEvent && (
                  <span className="pl-time" title={fullTime(dev.lastDeviceEvent.ts)}>
                    {formatEventTime(dev.lastDeviceEvent.ts)}
                  </span>
                )}
              </div>
            </Fragment>
          )
        }) : (
          <Fragment>
            <div className="pl-cell pl-col-udp pl-s-dim">
              <span className="pl-none">—</span>
            </div>
            <div className="pl-cell pl-col-device pl-s-dim">
              <span className="pl-none">no devices</span>
            </div>
          </Fragment>
        )}
      </div>

      {/* ── Action bar (expanded only) ── */}
      {expanded && (
        <div className="pl-actions">
          {onEditTemplate && (
            <button className="pl-btn pl-btn-edit" onClick={() => onEditTemplate(row.templateId)}>
              ✎ Edit
            </button>
          )}
          <button className="pl-btn pl-btn-grab" onClick={() => onGrabNow(row.templateId)}>
            ↺ Grab Now
          </button>
          {devs.map(dev => (
            onConfigDevice && (
              <button
                key={dev.id}
                className="pl-btn pl-btn-config"
                onClick={() => onConfigDevice(dev.id)}
              >
                ⚙ {dev.name}
              </button>
            )
          ))}
        </div>
      )}
    </div>
  )
})

// ── PipelineView ──────────────────────────────────────────────────────────

export default function PipelineView({ onEditTemplate, onConfigDevice }) {
  const [rows,     setRows]     = useState([])
  const [loading,  setLoading]  = useState(true)
  const [apiError, setApiError] = useState(null)
  const [logCtx,   setLogCtx]   = useState(null)
  const [expanded, setExpanded] = useState(new Set())
  const rowsRef      = useRef([])
  const reloadTimer  = useRef(null)

  const load = useCallback(() => {
    setLoading(true)
    setApiError(null)
    authFetch('/api/pipeline/summary')
      .then(r => {
        if (!r)    { setApiError('No response'); return null }
        if (!r.ok) { setApiError(`HTTP ${r.status}`); return null }
        return r.json()
      })
      .then(data => {
        if (!Array.isArray(data)) return
        const merged = mergeRows(rowsRef.current, data)
        rowsRef.current = merged
        setRows(merged)
      })
      .catch(err => setApiError(String(err)))
      .finally(() => setLoading(false))
  }, [])

  // Debounced reload — batches rapid SignalR events into a single fetch.
  const scheduleReload = useCallback((delayMs = 800) => {
    clearTimeout(reloadTimer.current)
    reloadTimer.current = setTimeout(load, delayMs)
  }, [load])

  // Initial load + 30s fallback polling.
  useEffect(() => {
    load()
    const id = setInterval(load, 30_000)
    return () => { clearInterval(id); clearTimeout(reloadTimer.current) }
  }, [load, scheduleReload])

  // SignalR — subscribe while Pipeline view is active (FlowCanvas is unmounted).
  const handleSignalR = useCallback((eventType, data) => {
    switch (eventType) {
      case 'GrabCompleted':
        // Wait briefly for the server to persist the event before fetching.
        scheduleReload(800)
        break
      case 'DeviceStatusChanged':
      case 'DeviceGrabCompleted':
        scheduleReload(400)
        break
      default:
        break
    }
  }, [scheduleReload])

  useSignalR(handleSignalR)

  const handleToggle = useCallback((templateId) => {
    setExpanded(prev => {
      const next = new Set(prev)
      next.has(templateId) ? next.delete(templateId) : next.add(templateId)
      return next
    })
  }, [])

  const handleGrabNow = useCallback((templateId) => {
    authFetch(`/api/templates/${templateId}/grab`, { method: 'POST' }).catch(() => {})
  }, [])

  return (
    <div className="pl-view">

      <div className="pl-toolbar">
        <span className="pl-title">Pipeline</span>
        <button className="pl-refresh-btn" onClick={load}>↺ Refresh</button>
      </div>

      <div className="pl-header">
        <div className="pl-hcell">Template</div>
        <div className="pl-hcell">Grabber</div>
        <div className="pl-hcell">UDP</div>
        <div className="pl-hcell">Device</div>
      </div>

      {loading && !rows.length && <div className="pl-empty">Loading…</div>}

      {!loading && apiError && (
        <div className="pl-empty pl-empty-err">API error: {apiError}</div>
      )}

      {!loading && !apiError && rows.length === 0 && (
        <div className="pl-empty">No templates</div>
      )}

      {rows.map(row => (
        <PipelineRow
          key={row.templateId}
          row={row}
          expanded={expanded.has(row.templateId)}
          onToggle={handleToggle}
          onOpenLog={setLogCtx}
          onEditTemplate={onEditTemplate}
          onConfigDevice={onConfigDevice}
          onGrabNow={handleGrabNow}
        />
      ))}

      {logCtx && (
        <EventLogDrawer ctx={logCtx} onClose={() => setLogCtx(null)} />
      )}
    </div>
  )
}
