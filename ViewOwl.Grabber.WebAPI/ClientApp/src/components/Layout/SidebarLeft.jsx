import Panel from '../HUD/Panel'
import StatusLED from '../HUD/StatusLED'
import DataRow from '../HUD/DataRow'
import './SidebarLeft.css'

/* ── ISA-101 severity tiers ──────────────────────────────────────────────── */
const HEALTH_SEVERITY = {
  failed:     { tier: 'p1', label: 'P1', title: 'Grab failed' },
  overdue:    { tier: 'p2', label: 'P2', title: 'Overdue'     },
  never:      { tier: 'p3', label: 'P3', title: 'Never grabbed' },
  unassigned: { tier: 'p4', label: 'P4', title: 'Unassigned'  },
}

function relativeTime(isoStr) {
  if (!isoStr) return '—'
  const diff = Math.floor((Date.now() - new Date(isoStr)) / 1000)
  if (diff < 60)   return 'just now'
  if (diff < 3600) return `${Math.floor(diff / 60)}m ago`
  if (diff < 86400) return `${Math.floor(diff / 3600)}h ago`
  return `${Math.floor(diff / 86400)}d ago`
}

function SidebarLeft({ dashboardState, healthData, events, collapsed, onToggle }) {
  const devices     = dashboardState?.devices ?? []
  const templates   = dashboardState?.templates ?? []
  const connections = dashboardState?.connections ?? []
  const online  = devices.filter(d => d.status === 'Online').length
  const offline = devices.length - online

  // Build alert list — only non-healthy templates, sorted by severity
  const alerts = (healthData ?? [])
    .filter(t => t.health !== 'healthy')
    .sort((a, b) => {
      const order = { failed: 0, overdue: 1, never: 2, unassigned: 3 }
      return (order[a.health] ?? 9) - (order[b.health] ?? 9)
    })

  const p1 = alerts.filter(t => t.health === 'failed'     && t.assignedDeviceCount > 0).length
  const p2 = alerts.filter(t => t.health === 'overdue'    && t.assignedDeviceCount > 0).length
  const p3 = alerts.filter(t => t.health === 'never'      && t.assignedDeviceCount > 0).length
  const p4 = alerts.filter(t => t.health === 'unassigned').length

  return (
    <aside className={`sidebar-left${collapsed ? ' sidebar-collapsed' : ''}`}>
      <button className="sidebar-collapse-btn sidebar-collapse-btn-left" onClick={onToggle} title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}>
        {collapsed ? '▶' : '◀'}
      </button>

      {!collapsed && <>

        {/* ── Alert Board — L1: severity counts ─────────── */}
        <Panel title="ALERT BOARD">
          <div className="alert-counts">
            <div className={`alert-count-cell${p1 > 0 ? ' ac-p1' : ' ac-dim'}`}>
              <span className="ac-value">{p1}</span>
              <span className="ac-label">P1</span>
            </div>
            <div className={`alert-count-cell${p2 > 0 ? ' ac-p2' : ' ac-dim'}`}>
              <span className="ac-value">{p2}</span>
              <span className="ac-label">P2</span>
            </div>
            <div className={`alert-count-cell${p3 > 0 ? ' ac-p3' : ' ac-dim'}`}>
              <span className="ac-value">{p3}</span>
              <span className="ac-label">P3</span>
            </div>
            <div className={`alert-count-cell${p4 > 0 ? ' ac-p4' : ' ac-dim'}`}>
              <span className="ac-value">{p4}</span>
              <span className="ac-label">P4</span>
            </div>
          </div>

          {/* ── Alert Board — L2: alert list ───────────── */}
          {alerts.length === 0
            ? <p className="alert-board-ok">● ALL TEMPLATES HEALTHY</p>
            : <div className="alert-list">
                {alerts.map(t => {
                  const sev = HEALTH_SEVERITY[t.health] ?? { tier: 'p4', label: '??', title: t.health }
                  return (
                    <div key={t.id} className={`alert-item alert-item-${sev.tier}`}>
                      <span className="alert-item-badge">{sev.label}</span>
                      <div className="alert-item-body">
                        <span className="alert-item-name">{t.name}</span>
                        <span className="alert-item-detail">
                          {sev.title}
                          {t.lastGrabAt ? ` · ${relativeTime(t.lastGrabAt)}` : ''}
                        </span>
                      </div>
                    </div>
                  )
                })}
              </div>
          }
        </Panel>

        {/* ── System status (concise counts) ────────────── */}
        <Panel title="SYSTEM STATUS">
          <DataRow label="Devices"     value={`${online}/${devices.length} online`} />
          <DataRow label="Templates"   value={templates.length} />
          <DataRow label="Connections" value={connections.length} />
        </Panel>

        {/* ── Device list ───────────────────────────────── */}
        <Panel title="DEVICES" className="sidebar-device-list">
          {devices.length === 0
            ? <p className="sidebar-empty">NO DEVICES</p>
            : devices.map(d => (
                <div key={d.id} className="device-list-item">
                  <StatusLED status={d.status === 'Online' ? 'online' : 'offline'} />
                  <span className="device-list-name">{d.name}</span>
                </div>
              ))
          }
        </Panel>

        {/* ── Event log ─────────────────────────────────── */}
        <Panel title="EVENT LOG" className="sidebar-event-log">
          {events.length === 0
            ? <p className="sidebar-empty">AWAITING EVENTS</p>
            : events.map((ev, i) => (
                <div key={i} className={`event-item event-${ev.type}`}>
                  <span className="event-time">{ev.time}</span>
                  <span className="event-msg">{ev.message}</span>
                </div>
              ))
          }
        </Panel>

      </>}
    </aside>
  )
}
export default SidebarLeft
