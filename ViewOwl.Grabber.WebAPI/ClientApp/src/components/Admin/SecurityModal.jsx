import { useState, useEffect, useCallback } from 'react'
import { confirm } from '../../utils/confirm'
import './SecurityModal.css'

/**
 * Admin-only security popup — security-event log ("who came, when, how often, how
 * rejected") + manual IP block / unblock. Wired to SecurityDashboardController
 * (/admin/security/*), which is gated by the X-Admin-Key header (separate secret from
 * the JWT, by deliberate design — an extra lock on the most dangerous panel). The key
 * is prompted here and held IN MEMORY only — never persisted to storage.
 */
const EVENT_TYPES = ['', 'BruteForce', 'RateLimit', 'ScannerProbe', 'InvalidToken', 'Blocked', 'SuspiciousActivity']

export default function SecurityModal({ onClose }) {
  const [key, setKey]         = useState('')
  const [unlocked, setUnlocked] = useState(false)
  const [error, setError]     = useState(null)

  const [events, setEvents]   = useState([])
  const [blocks, setBlocks]   = useState([])
  const [typeFilter, setTypeFilter] = useState('')
  const [ipFilter, setIpFilter]     = useState('')

  const [blockIp, setBlockIp]         = useState('')
  const [blockReason, setBlockReason] = useState('')
  const [blockHours, setBlockHours]   = useState(24)

  // Raw fetch with the in-memory admin key (NOT authFetch — different scheme + base path).
  const secFetch = useCallback((path, opts = {}) =>
    fetch(`/admin/security${path}`, {
      ...opts,
      headers: { 'X-Admin-Key': key, ...(opts.headers || {}) },
    }), [key])

  const loadEvents = useCallback(async () => {
    const qs = new URLSearchParams({ page: '1', pageSize: '60' })
    if (typeFilter) qs.set('eventType', typeFilter)
    if (ipFilter.trim()) qs.set('ip', ipFilter.trim())
    const r = await secFetch(`/events?${qs}`)
    if (r.status === 401) { setError('admin key rejected or not configured on the server'); setUnlocked(false); return false }
    if (!r.ok) { setError(`events: HTTP ${r.status}`); return false }
    const d = await r.json()
    setEvents(d.items || [])
    setError(null)
    return true
  }, [secFetch, typeFilter, ipFilter])

  const loadBlocks = useCallback(async () => {
    const r = await secFetch('/blocks')
    if (r.ok) setBlocks(await r.json())
  }, [secFetch])

  const unlock = async () => {
    if (!key) { setError('enter the admin key'); return }
    const ok = await loadEvents()
    if (ok) { setUnlocked(true); loadBlocks() }
  }

  useEffect(() => {
    const onKey = (e) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  // Re-query events when filters change (only once unlocked).
  useEffect(() => { if (unlocked) loadEvents() }, [typeFilter, ipFilter]) // eslint-disable-line react-hooks/exhaustive-deps

  const doBlock = async () => {
    if (!blockIp.trim()) { setError('enter an IP to block'); return }
    const r = await secFetch(`/blocks/${encodeURIComponent(blockIp.trim())}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ reason: blockReason.trim() || 'manual', hours: Number(blockHours) || 0 }),
    })
    if (r.ok) { setBlockIp(''); setBlockReason(''); loadBlocks() }
    else setError(`block: HTTP ${r.status}`)
  }

  const unblock = async (ip) => {
    const ok = await confirm(`Unblock ${ip}?`, { title: 'UNBLOCK IP', yes: 'UNBLOCK' })
    if (!ok) return
    const r = await secFetch(`/blocks/${encodeURIComponent(ip)}`, { method: 'DELETE' })
    if (r.ok) loadBlocks()
  }

  const hhmmss = (iso) => new Date(iso).toLocaleTimeString('en-GB')
  const sevClass = (t) =>
    (t === 'Blocked' || t === 'BruteForce') ? 'sm-t-err'
      : (t === 'ScannerProbe' || t === 'SuspiciousActivity') ? 'sm-t-warn' : 'sm-t-dim'

  return (
    <div className="sm-overlay" onClick={e => { if (e.target === e.currentTarget) onClose() }}>
      <div className="sm-modal">
        <div className="sm-header">
          <span className="sm-title">SECURITY</span>
          <span className="sm-grow" />
          <button className="sm-close" onClick={onClose} aria-label="Close">✕</button>
        </div>

        {!unlocked ? (
          <div className="sm-lock">
            <div className="sm-lock-title">ADMIN KEY REQUIRED</div>
            <p className="sm-lock-note">
              The security panel is gated by a separate admin key (held in memory only, never stored).
            </p>
            <div className="sm-lock-row">
              <input className="sm-input" type="password" placeholder="X-Admin-Key" value={key}
                     autoFocus autoComplete="off"
                     onChange={e => setKey(e.target.value)}
                     onKeyDown={e => { if (e.key === 'Enter') unlock() }} />
              <button className="sm-btn-primary" onClick={unlock}>UNLOCK</button>
            </div>
            {error && <div className="sm-error">{error}</div>}
          </div>
        ) : (
          <div className="sm-body">
            {error && <div className="sm-error">{error}</div>}

            {/* ── IP blocks ── */}
            <div className="sm-section">
              <div className="sm-section-title">IP BLOCKS</div>
              <div className="sm-block-form">
                <input className="sm-input" placeholder="IP address" value={blockIp}
                       onChange={e => setBlockIp(e.target.value)} />
                <input className="sm-input" placeholder="reason" value={blockReason}
                       onChange={e => setBlockReason(e.target.value)} />
                <input className="sm-input sm-input-num" type="number" min="0" title="hours (0 = permanent)"
                       value={blockHours} onChange={e => setBlockHours(e.target.value)} />
                <button className="sm-btn-warn" onClick={doBlock}>BLOCK</button>
              </div>
              <table className="sm-table">
                <thead><tr><th>IP</th><th>REASON</th><th>UNTIL</th><th>SRC</th><th aria-label="actions" /></tr></thead>
                <tbody>
                  {blocks.length === 0 && <tr><td colSpan="5" className="sm-dim">no active blocks</td></tr>}
                  {blocks.map(b => (
                    <tr key={b.id}>
                      <td className="sm-ip">{b.ipAddress}</td>
                      <td className="sm-dim">{b.reason}</td>
                      <td className="sm-dim">{b.blockedUntil ? new Date(b.blockedUntil).toISOString().slice(0, 16).replace('T', ' ') : 'PERMANENT'}</td>
                      <td className="sm-dim">{b.isManual ? 'MANUAL' : 'AUTO'}</td>
                      <td className="sm-actions"><button className="sm-unblock" onClick={() => unblock(b.ipAddress)}>UNBLOCK</button></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>

            {/* ── Event log ── */}
            <div className="sm-section">
              <div className="sm-section-head">
                <div className="sm-section-title">SECURITY EVENTS</div>
                <select className="sm-select" value={typeFilter} onChange={e => setTypeFilter(e.target.value)}>
                  {EVENT_TYPES.map(t => <option key={t} value={t}>{t || 'ALL TYPES'}</option>)}
                </select>
                <input className="sm-input sm-input-ip" placeholder="filter IP" value={ipFilter}
                       onChange={e => setIpFilter(e.target.value)} />
              </div>
              <table className="sm-table">
                <thead><tr><th>TIME</th><th>IP</th><th>TYPE</th><th>METHOD</th><th>ENDPOINT</th><th>ST</th></tr></thead>
                <tbody>
                  {events.length === 0 && <tr><td colSpan="6" className="sm-dim">no events</td></tr>}
                  {events.map(e => (
                    <tr key={e.id}>
                      <td className="sm-dim">{hhmmss(e.createdAt)}</td>
                      <td className="sm-ip">{e.ipAddress}</td>
                      <td className={sevClass(e.eventType)}>{e.eventType}</td>
                      <td className="sm-dim">{e.method}</td>
                      <td className="sm-endpoint" title={e.endpoint}>{e.endpoint}</td>
                      <td className="sm-dim">{e.statusCode}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}
      </div>
    </div>
  )
}
