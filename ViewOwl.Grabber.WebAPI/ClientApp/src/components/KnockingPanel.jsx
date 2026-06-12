import { useState } from 'react'
import './KnockingPanel.css'

const REASON_LABELS = {
  'bad-token':   'BAD TOKEN',
  'no-device':   'NOT REGISTERED',
  'no-template': 'NO TEMPLATE',
  'no-frame':    'NO FRAME',
}

const DISPLAY_OPTIONS = [
  { id: '480x320-st',  label: '480×320 ST7796'  },
  { id: '320x240-ili', label: '320×240 ILI9341' },
]

const formatAge = (iso) => {
  const secs = Math.floor((Date.now() - new Date(iso).getTime()) / 1000)
  if (secs < 60)  return `${secs}s ago`
  const mins = Math.floor(secs / 60)
  if (mins < 60)  return `${mins}m ago`
  return `${Math.floor(mins / 60)}h ago`
}

function KnockingPanel({ devices, onDismiss, onRegister }) {
  const [collapsed, setCollapsed]     = useState(false)
  const [registering, setRegistering] = useState({})       // ip → true while in-flight
  const [displaySel, setDisplaySel]   = useState({})       // ip → selected display id
  const [nameSel, setNameSel]         = useState({})       // ip → entered device name

  if (!devices || devices.length === 0) return null

  const getDisplay = (ip) => displaySel[ip] ?? '480x320-st'
  const getName    = (ip) => nameSel[ip] ?? ''

  const handleRegister = async (d) => {
    const name = getName(d.ip).trim()
    if (!name) return
    setRegistering(prev => ({ ...prev, [d.ip]: true }))
    try {
      await onRegister?.(d, getDisplay(d.ip), name)
    } finally {
      setRegistering(prev => ({ ...prev, [d.ip]: false }))
    }
  }

  return (
    <div className="knocking-panel">
      <div className="kp-header" onClick={() => setCollapsed(c => !c)}>
        <span className="kp-dot" />
        <span className="kp-title">KNOCKING</span>
        <span className="kp-count">{devices.length}</span>
        <span className="kp-toggle">{collapsed ? '▸' : '▾'}</span>
      </div>

      {!collapsed && (
        <div className="kp-list">
          {devices.map(d => (
            <div key={d.ip} className="kp-entry">
              <div className="kp-entry-top">
                <span className="kp-ip">{d.ip}</span>
                <span className="kp-reason">{REASON_LABELS[d.reason] ?? d.reason.toUpperCase()}</span>
                <button className="kp-dismiss" onClick={() => onDismiss(d.ip)} title="Dismiss">×</button>
              </div>
              <div className="kp-entry-bottom">
                {d.deviceName
                  ? <span className="kp-device-name">{d.deviceName}</span>
                  : <span className="kp-token">{d.token === '?' ? 'unknown token' : `${d.token.slice(0, 8)}…`}</span>
                }
                <span className="kp-meta">×{d.count} · {formatAge(d.lastSeen)}</span>
              </div>
              {/* Registration row for unregistered devices with a known token */}
              {d.reason === 'no-device' && d.token && d.token !== '?' && (
                <>
                  <input
                    className="kp-name-input"
                    placeholder="Device name…"
                    value={getName(d.ip)}
                    onChange={e => setNameSel(prev => ({ ...prev, [d.ip]: e.target.value }))}
                    onKeyDown={e => { if (e.key === 'Enter') handleRegister(d) }}
                    disabled={registering[d.ip]}
                    spellCheck={false}
                  />
                  <div className="kp-register-row">
                    <select
                      className="kp-display-select"
                      value={getDisplay(d.ip)}
                      onChange={e => setDisplaySel(prev => ({ ...prev, [d.ip]: e.target.value }))}
                      disabled={registering[d.ip]}
                    >
                      {DISPLAY_OPTIONS.map(o => (
                        <option key={o.id} value={o.id}>{o.label}</option>
                      ))}
                    </select>
                    <button
                      className="kp-register-btn"
                      disabled={registering[d.ip] || !getName(d.ip).trim()}
                      onClick={() => handleRegister(d)}
                    >
                      {registering[d.ip] ? 'REGISTERING…' : '+ REGISTER'}
                    </button>
                  </div>
                </>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

export default KnockingPanel
