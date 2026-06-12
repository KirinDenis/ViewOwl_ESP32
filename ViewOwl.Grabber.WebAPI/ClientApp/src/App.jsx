import { useState, useEffect, useCallback, useRef } from 'react'
import { createPortal } from 'react-dom'
import Header from './components/Layout/Header'
import SidebarLeft from './components/Layout/SidebarLeft'
import SidebarRight from './components/Layout/SidebarRight'
import Footer from './components/Layout/Footer'
import FlowCanvas from './components/FlowCanvas'
import TemplateManagerModal from './components/Templates/TemplateManagerModal'
import ControllerConfigModal from './components/Controllers/ControllerConfigModal'
import BurnModal from './components/Burn/BurnModal'
import ConfirmModal from './components/ConfirmModal/ConfirmModal'
import { authFetch, getToken } from './utils/auth'
import './App.css'

const MAX_EVENTS = 40
const THEME_KEY = 'vo-theme'
const TOKEN_KEY = 'viewowl_token'
// Delay before auto-reload after "complete" notification (ms)
const DEPLOY_RELOAD_DELAY_MS = 4000

function App() {
  const [dashboardState, setDashboardState] = useState(null)
  const [loading, setLoading] = useState(true)
  const [error, setError]     = useState(null)
  const [selectedNode, setSelectedNode]     = useState(null)
  const [connectionState, setConnectionState] = useState('Disconnected')
  const [events, setEvents]   = useState([])
  // null | { phase: 'starting'|'complete', commit: string|null }
  const [deployNotice, setDeployNotice]     = useState(null)
  // Theme — shared with admin panel via the same localStorage key
  const [theme, setTheme] = useState(() => localStorage.getItem(THEME_KEY) || 'dark')

  useEffect(() => {
    document.documentElement.setAttribute('data-bs-theme', theme)
    localStorage.setItem(THEME_KEY, theme)
  }, [theme])

  const handleToggleTheme = useCallback(() => {
    setTheme(prev => prev === 'dark' ? 'light' : 'dark')
  }, [])

  const handleLogout = useCallback(() => {
    localStorage.removeItem(TOKEN_KEY)
    window.location.href = '/login.html'
  }, [])

  // Sidebar collapse — persisted in localStorage, browser-local only (not synced)
  const [leftCollapsed,  setLeftCollapsed]  = useState(() => localStorage.getItem('vo:sidebar-left')  === 'collapsed')
  const [rightCollapsed, setRightCollapsed] = useState(() => localStorage.getItem('vo:sidebar-right') === 'collapsed')

  const toggleLeft  = useCallback(() => setLeftCollapsed(v  => { const next = !v;  localStorage.setItem('vo:sidebar-left',  next ? 'collapsed' : 'expanded'); return next }), [])
  const toggleRight = useCallback(() => setRightCollapsed(v => { const next = !v;  localStorage.setItem('vo:sidebar-right', next ? 'collapsed' : 'expanded'); return next }), [])

  // Template manager modal
  const [templateManagerOpen, setTemplateManagerOpen] = useState(false)

  const [burnOpen, setBurnOpen] = useState(false)

  const [createdDevice, setCreatedDevice] = useState(null)

  const pushEvent = useCallback((message, type = 'info') => {
    const time = new Date().toLocaleTimeString('en-GB')
    setEvents(prev => [{ time, message, type }, ...prev].slice(0, MAX_EVENTS))
  }, [])

  // Optimistic update — removes a template from dashboardState immediately
  // so the sidebar Templates counter updates before the async reload completes
  const handleTemplateDeleted = useCallback((templateId) => {
    setDashboardState(prev => !prev ? prev : {
      ...prev,
      templates: prev.templates?.filter(t => t.id !== templateId) ?? []
    })
  }, [])

  // Optimistic update — removes a device (and its connections) from dashboardState
  // immediately so the sidebar Devices counter updates without a full reload
  const handleDeviceDeleted = useCallback((deviceId) => {
    setDashboardState(prev => !prev ? prev : {
      ...prev,
      devices:     prev.devices?.filter(d => d.id !== deviceId) ?? [],
      connections: prev.connections?.filter(c => c.deviceId !== deviceId) ?? [],
    })
  }, [])

  const loadDashboard = useCallback(() => {
    if (!getToken()) { window.location.href = '/login.html'; return }
    setLoading(true)
    authFetch('/api/dashboard/state')
      .then(res => { if (!res) return; if (!res.ok) throw new Error(`HTTP ${res.status}`); return res.json() })
      .then(data => { if (!data) return; setDashboardState(data); setLoading(false) })
      .catch(err => { setError(err.message); setLoading(false) })
  }, [])

  useEffect(() => { loadDashboard() }, [loadDashboard])

  const handleConnectionStateChange = useCallback((state) => {
    setConnectionState(state)
    if (state === 'Connected')    pushEvent('SignalR connected', 'success')
    if (state === 'Disconnected') pushEvent('SignalR disconnected', 'warning')
    if (state === 'Reconnecting') pushEvent('SignalR reconnecting...', 'warning')
  }, [pushEvent])

  // ── Template health — drives alert board and header alarm counts ────────────
  const [healthData, setHealthData] = useState([])

  const loadHealth = useCallback(() => {
    authFetch('/api/health/templates')
      .then(res => { if (!res?.ok) return; return res.json() })
      .then(data => { if (Array.isArray(data)) setHealthData(data) })
      .catch(() => {})
  }, [])

  // Stable ref so handleSignalREvent can call loadHealth without it being a dep
  const loadHealthRef = useRef(loadHealth)
  useEffect(() => { loadHealthRef.current = loadHealth }, [loadHealth])

  // Fetch health on mount, then every 60 seconds
  useEffect(() => {
    loadHealth()
    const id = setInterval(loadHealth, 60_000)
    return () => clearInterval(id)
  }, [loadHealth])

  // Compute ISA-101 alarm counts from health data
  const alarmCounts = {
    p1: healthData.filter(t => t.health === 'failed' && t.assignedDeviceCount > 0).length,
    p2: healthData.filter(t => t.health === 'overdue' && t.assignedDeviceCount > 0).length,
    p3: healthData.filter(t => t.health === 'never' && t.assignedDeviceCount > 0).length,
    p4: healthData.filter(t => t.health === 'unassigned').length,
  }

  const handleSignalREvent = useCallback((eventType, data) => {
    // Refresh health data when a grab completes (template health state may have changed)
    if (eventType === 'GrabCompleted') loadHealthRef.current()
    switch (eventType) {
      case 'DeviceStatusChanged':
        pushEvent(`${data.deviceId}: ${data.status}`, data.status === 'Online' ? 'success' : 'warning')
        // Keep the right-sidebar inspector in sync when the selected device changes status
        setSelectedNode(prev =>
          prev?.type === 'device' && prev?.data?.id === data.deviceId
            ? { ...prev, data: { ...prev.data, status: data.status, lastSeenAt: data.lastSeenAt, ipAddress: data.ipAddress ?? prev.data.ipAddress } }
            : prev
        )
        break
      case 'DevicePing':
        pushEvent(`Ping from device #${data.deviceId}`, 'info')
        // Update Last Seen in the inspector without requiring the user to re-click the node
        setSelectedNode(prev =>
          prev?.type === 'device' && prev?.data?.id === data.deviceId
            ? { ...prev, data: { ...prev.data, lastSeenAt: data.timestamp } }
            : prev
        )
        break
      case 'GrabberUpdate':
        pushEvent('Grabber update received', 'info')
        break
      case 'DeviceGrabCompleted':
        pushEvent(`Grab complete: device #${data.deviceId}`, 'success')
        break
      case 'GrabCompleted':
        if (data.success) pushEvent(`Template #${data.templateId} grab OK`, 'success')
        else pushEvent(`Template #${data.templateId} grab failed`, 'warning')
        break
      case 'TemplateUpdated':
        pushEvent(`Template updated: #${data.id}`, 'info')
        break
      case 'DeployNotification':
        setDeployNotice({ phase: data.phase, commit: data.commit ?? null })
        if (data.phase === 'complete') {
          // Auto-reload after a short delay so the banner is briefly visible
          setTimeout(() => window.location.reload(), DEPLOY_RELOAD_DELAY_MS)
        }
        break
      default:
        break
    }
  }, [pushEvent, setSelectedNode])

  const handleEditTemplates = useCallback(() => {
    setTemplateManagerOpen(true)
  }, [])

  // Only reload the dashboard when something was actually saved or deleted.
  const handleCloseTemplateManager = useCallback((changed = false) => {
    setTemplateManagerOpen(false)
    if (changed) loadDashboard()
  }, [loadDashboard])

  const devicesOnline = dashboardState?.devices?.filter(d => d.status === 'Online').length ?? 0
  const devicesTotal  = dashboardState?.devices?.length ?? 0

  if (loading) {
    return (
      <div className="fullscreen-center">
        <div className="grid-bg" style={{position:'fixed', inset:0, zIndex:0}} />
  
        <p className="loading-title glow-text">VIEWOWL</p>
        <p className="loading-sub">INITIALISING COMMAND CENTER</p>
      </div>
    )
  }

  if (error) {
    return (
      <div className="fullscreen-center">
        <div className="grid-bg" style={{position:'fixed', inset:0, zIndex:0}} />
  
        <div className="error-box">
          <p className="error-title">SYSTEM ERROR</p>
          <p className="error-msg">{error}</p>
          <a className="error-link" href="/login.html">RETURN TO LOGIN</a>
        </div>
      </div>
    )
  }

  return (
    <div
        className="app-layout"
        style={{ gridTemplateColumns: `${leftCollapsed ? '32px' : '280px'} 1fr ${rightCollapsed ? '32px' : '280px'}` }}
      >


      <Header
        connectionState={connectionState}
        devicesOnline={devicesOnline}
        devicesTotal={devicesTotal}
        alarmCounts={alarmCounts}
        onEditTemplates={handleEditTemplates}
        onBurn={() => setBurnOpen(true)}
        theme={theme}
        onToggleTheme={handleToggleTheme}
        onLogout={handleLogout}
      />

      <SidebarLeft
        dashboardState={dashboardState}
        healthData={healthData}
        events={events}
        collapsed={leftCollapsed}
        onToggle={toggleLeft}
      />

      <div className="main-canvas">
        <div className="canvas-area">
          <div className="grid-bg" />
          <FlowCanvas
            dashboardState={dashboardState}
            onNodeSelect={setSelectedNode}
            onConnectionStateChange={handleConnectionStateChange}
            onSignalREvent={handleSignalREvent}
            onRefreshDashboard={loadDashboard}
            onTemplateDeleted={handleTemplateDeleted}
            onDeviceDeleted={handleDeviceDeleted}
            onDeviceRegistered={(device) => {
              loadDashboard()
              if (device?.id) setSelectedNode({ type: 'device', data: device })
            }}
          />
        </div>
      </div>

      <SidebarRight selectedNode={selectedNode} collapsed={rightCollapsed} onToggle={toggleRight} />

      <Footer connectionState={connectionState} />

      {/* Burn — flash firmware to ESP32 via Web Serial */}
      {burnOpen && createPortal(
        <BurnModal
          onClose={() => setBurnOpen(false)}
          onDone={(deviceId) => {
            // Always reload dashboard so the new device tile appears.
            loadDashboard()
            // Optionally focus the new device in the right sidebar.
            if (deviceId) {
              authFetch(`/api/devices/${deviceId}`)
                .then(r => r?.json())
                .then(device => {
                  if (device?.id) setSelectedNode({ type: 'device', data: device })
                })
                .catch(() => {})
            }
          }}
        />,
        document.body
      )}

      {/* Template Manager — admin-only CRUD for all templates */}
      {templateManagerOpen && createPortal(
        <TemplateManagerModal
          onClose={handleCloseTemplateManager}
        />,
        document.body
      )}

      {/* Add controller — step 2: show config modal with the new device's token */}
      {createdDevice && createPortal(
        <ControllerConfigModal
          device={createdDevice}
          liveUptime={null}
          liveRssi={null}
          onClose={() => setCreatedDevice(null)}
        />,
        document.body
      )}

      {/* Custom confirm dialog — replaces native window.confirm across the app */}
      <ConfirmModal />

      {/* Deploy notification overlay — shown to all users during CI/CD deploy */}
      {deployNotice && createPortal(
        <div style={{
          position: 'fixed', inset: 0, zIndex: 9999,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
          background: 'rgba(0,0,0,0.75)', backdropFilter: 'blur(4px)',
        }}>
          <div style={{
            fontFamily: 'monospace', textAlign: 'center', color: '#00e5ff',
            border: '1px solid rgba(0,229,255,0.4)', borderRadius: 8,
            padding: '32px 48px', background: 'rgba(4,12,24,0.95)',
            boxShadow: '0 0 40px rgba(0,229,255,0.2)',
          }}>
            {deployNotice.phase === 'starting' ? (
              <>
                <div style={{ fontSize: 32, marginBottom: 12 }}>⚙</div>
                <div style={{ fontSize: 18, letterSpacing: 3 }}>DEPLOYING</div>
                {deployNotice.commit && (
                  <div style={{ fontSize: 11, marginTop: 8, opacity: 0.6 }}>
                    commit {deployNotice.commit}
                  </div>
                )}
                <div style={{ fontSize: 12, marginTop: 16, opacity: 0.5 }}>
                  Server is updating — please wait a moment
                </div>
              </>
            ) : (
              <>
                <div style={{ fontSize: 32, marginBottom: 12 }}>✓</div>
                <div style={{ fontSize: 18, letterSpacing: 3 }}>DEPLOYED</div>
                {deployNotice.commit && (
                  <div style={{ fontSize: 11, marginTop: 8, opacity: 0.6 }}>
                    commit {deployNotice.commit}
                  </div>
                )}
                <div style={{ fontSize: 12, marginTop: 16, opacity: 0.5 }}>
                  Reloading dashboard…
                </div>
              </>
            )}
          </div>
        </div>,
        document.body
      )}
    </div>
  )
}
export default App
