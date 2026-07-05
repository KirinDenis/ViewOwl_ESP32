import { useState, useEffect } from 'react'
import StatusLED from '../HUD/StatusLED'
import { useAuth } from '../../utils/AuthContext'
import './Header.css'

function Header({ connectionState, devicesOnline, devicesTotal, alarmCounts, onEditTemplates, onBurn, onUsers, onSecurity, theme, onToggleTheme, onLogout }) {
  const { isAdmin } = useAuth()
  const [time, setTime] = useState(new Date())

  useEffect(() => {
    const t = setInterval(() => setTime(new Date()), 1000)
    return () => clearInterval(t)
  }, [])

  const pad = (n) => String(n).padStart(2, '0')
  const timeStr = `${pad(time.getHours())}:${pad(time.getMinutes())}:${pad(time.getSeconds())}`
  const dateStr = time.toISOString().slice(0, 10)

  const counts = alarmCounts ?? { p1: 0, p2: 0, p3: 0, p4: 0 }
  const hasAlarms = counts.p1 > 0 || counts.p2 > 0 || counts.p3 > 0

  return (
    <header className="app-header hud-panel">

      {/* ── Branding ───────────────────────────────────── */}
      <div className="header-brand">
        <span className="header-logo">VIEWOWL</span>
        <span className="header-sub">PIPELINE MONITOR</span>
      </div>

      <div className="header-vsep" />

      {/* ── Status strip ───────────────────────────────── */}
      <div className="header-status">
        <StatusLED status={devicesOnline > 0 ? 'online' : 'offline'} label={`${devicesOnline}/${devicesTotal} ONLINE`} />
        <div className="header-divider" />
        <StatusLED status={connectionState} label="REALTIME" />
        <div className="header-divider" />
        <span className="header-datetime">{dateStr}&nbsp;&nbsp;{timeStr}</span>
      </div>

      {/* ── Alarm count badges (ISA-101 — only visible when non-zero) ─── */}
      {hasAlarms && (
        <>
          <div className="header-divider header-alarm-sep" />
          <div className="header-alarms">
            {counts.p1 > 0 && (
              <span className="alarm-badge alarm-p1" title="P1 Critical — grab failed on assigned template">
                P1&nbsp;{counts.p1}
              </span>
            )}
            {counts.p2 > 0 && (
              <span className="alarm-badge alarm-p2" title="P2 High — template overdue (not grabbed in 10+ min)">
                P2&nbsp;{counts.p2}
              </span>
            )}
            {counts.p3 > 0 && (
              <span className="alarm-badge alarm-p3" title="P3 Warning — assigned template never grabbed">
                P3&nbsp;{counts.p3}
              </span>
            )}
          </div>
        </>
      )}

      <div className="header-spacer" />

      {/* ── Action buttons ─────────────────────────────── */}
      <div className="header-toolbar">
        {isAdmin && (
          <>
            <button className="header-tool-btn header-tool-btn-templates" onClick={onEditTemplates}>⊞ TEMPLATES</button>
            <button className="header-tool-btn" onClick={onUsers}>USERS</button>
            <button className="header-tool-btn" onClick={onSecurity}>SECURITY</button>
          </>
        )}
        <button className="header-tool-btn header-tool-btn-burn" onClick={onBurn}>⚡ BURN</button>
        <a className="header-tool-btn header-tool-btn-admin" href="/dashboard.html">
          ADMIN
        </a>
        <div className="header-divider" />
        <button className="header-icon-btn" onClick={onToggleTheme} title="Toggle dark/light mode">
          {theme === 'dark' ? '☀️' : '🌙'}
        </button>
        <button className="header-icon-btn header-icon-btn-logout" onClick={onLogout} title="Sign out">
          ⏻
        </button>
      </div>

    </header>
  )
}
export default Header
