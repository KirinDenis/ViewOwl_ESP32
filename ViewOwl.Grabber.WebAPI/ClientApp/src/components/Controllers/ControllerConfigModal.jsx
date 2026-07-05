import { useState, useEffect, useCallback } from 'react'
import { createPortal } from 'react-dom'
import { authFetch } from '../../utils/auth'
import { confirm } from '../../utils/confirm'
import BurnModal from '../Burn/BurnModal'
import SerialTerminalModal from '../Serial/SerialTerminalModal'
import MiniLineChart from './MiniLineChart'
import './ControllerConfigModal.css'

const formatUptime = (s) => {
  if (!s) return '—'
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  return h > 0 ? `${h}h ${m}m` : `${m}m`
}

const rssiLabel = (rssi) => {
  if (rssi == null) return '—'
  if (rssi >= -50) return 'Excellent'
  if (rssi >= -67) return 'Good'
  if (rssi >= -75) return 'Fair'
  if (rssi >= -85) return 'Poor'
  return 'Very poor'
}

function ControllerConfigModal({ device, liveUptime, liveRssi, onClose, onNameSaved }) {
  const [name, setName]                     = useState(device.name)
  const [savedName, setSavedName]           = useState(device.name)  // tracks last-persisted name
  const [selectedTemplateId, setTemplateId] = useState(device.activeTemplateId ?? '')
  const [templates, setTemplates]           = useState([])
  const [pings, setPings]                   = useState([])
  const [saving, setSaving]                 = useState(false)
  const [saveStatus, setSaveStatus]         = useState(null)  // null | 'saved' | 'error'
  const [restarting, setRestarting]         = useState(false)
  const [reburnOpen, setReburnOpen]         = useState(false)
  const [termOpen,   setTermOpen]           = useState(false)
  const [tokenInput, setTokenInput]         = useState(device.token ?? '')
  const [tokenSaving, setTokenSaving]       = useState(false)
  const [tokenStatus, setTokenStatus]       = useState(null)  // null | 'saved' | 'error'

  // Load templates list for dropdown
  useEffect(() => {
    authFetch('/api/templates')
      .then(r => r?.json())
      .then(d => { if (Array.isArray(d)) setTemplates(d) })
      .catch(() => {})
  }, [])

  // Load ping history for chart
  const loadStats = useCallback(() => {
    authFetch(`/api/devices/${device.id}/stats`)
      .then(r => r?.json())
      .then(d => { if (d?.recentPings) setPings(d.recentPings) })
      .catch(() => {})
  }, [device.id])

  useEffect(() => { loadStats() }, [loadStats])

  // Keyboard: Esc closes
  useEffect(() => {
    const onKey = (e) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  const handleSaveName = async () => {
    setSaving(true)
    setSaveStatus(null)
    try {
      const res = await authFetch(`/api/devices/${device.id}/name`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name }),
      })
      if (!res?.ok) throw new Error()
      setSavedName(name)
      setSaveStatus('saved')
      onNameSaved?.(name)
    } catch {
      setSaveStatus('error')
    } finally {
      setSaving(false)
      setTimeout(() => setSaveStatus(null), 2500)
    }
  }

  const handleSaveTemplate = async () => {
    const templateId = selectedTemplateId === '' ? null : Number(selectedTemplateId)
    await authFetch(`/api/devices/${device.id}/active-template`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ templateId }),
    })
  }

  const handleSaveToken = async () => {
    const trimmed = tokenInput.trim()
    // Must be a 32-char hex string (GUID N format, no dashes)
    if (!/^[0-9a-fA-F]{32}$/.test(trimmed)) {
      setTokenStatus('error')
      setTimeout(() => setTokenStatus(null), 2500)
      return
    }
    setTokenSaving(true)
    setTokenStatus(null)
    try {
      const res = await authFetch(`/api/devices/${device.id}/token`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ token: trimmed.toLowerCase() }),
      })
      if (!res?.ok) throw new Error()
      setTokenStatus('saved')
    } catch {
      setTokenStatus('error')
    } finally {
      setTokenSaving(false)
      setTimeout(() => setTokenStatus(null), 2500)
    }
  }

  const handleRestart = async () => {
    if (!await confirm(`Restart device "${device.name}"?\nThis will interrupt the current frame delivery.`, { title: 'RESTART DEVICE', danger: true })) return
    setRestarting(true)
    try {
      await authFetch(`/api/devices/${device.id}/restart`, { method: 'POST' })
    } finally {
      setRestarting(false)
    }
  }

  const isOnline   = device.status === 'Online'
  const nameDirty  = name !== savedName
  const tmplDirty  = String(selectedTemplateId) !== String(device.activeTemplateId ?? '')

  return (
    <>
    <div className="cc-overlay" onClick={e => { if (e.target === e.currentTarget) onClose() }}>
      <div className="cc-modal hud-panel">

        {/* Header */}
        <div className="cc-header">
          <div className="cc-header-left">
            <span className={`cc-status-dot ${isOnline ? 'cc-dot-online' : 'cc-dot-offline'}`} />
            <span className="cc-title">CONTROLLER CONFIG</span>
            <span className="cc-device-name">{savedName}</span>
          </div>
          <button className="cc-close-btn" onClick={onClose} title="Close (Esc)">✕</button>
        </div>

        {/* Body */}
        <div className="cc-body">

          {/* Left: Device info + template */}
          <div className="cc-left">

            {/* Device Information */}
            <div className="cc-section">
              <div className="cc-section-title">DEVICE INFORMATION</div>

              <div className="cc-field">
                <label className="cc-label">NAME</label>
                <div className="cc-field-row">
                  <input
                    className="cc-input"
                    value={name}
                    onChange={e => setName(e.target.value)}
                    spellCheck={false}
                  />
                  {nameDirty && (
                    <button
                      className="cc-btn cc-btn-save"
                      onClick={handleSaveName}
                      disabled={saving}
                    >
                      {saveStatus === 'saved' ? '✓' : saveStatus === 'error' ? '✗' : 'SAVE'}
                    </button>
                  )}
                </div>
              </div>

              <div className="cc-row">
                <span className="cc-label">IP ADDRESS</span>
                <span className="cc-value">{device.ipAddress ?? '—'}</span>
              </div>

              <div className="cc-row">
                <span className="cc-label">STATUS</span>
                <span className={`cc-value ${isOnline ? 'cc-online' : 'cc-offline'}`}>
                  {device.status ?? 'Unknown'}
                </span>
              </div>

              <div className="cc-row">
                <span className="cc-label">DISPLAY</span>
                <span className="cc-value">
                  {device.displayWidth && device.displayHeight
                    ? `${device.displayWidth}×${device.displayHeight}`
                    : '—'}
                  {device.displayType ? ` (${device.displayType})` : ''}
                </span>
              </div>

              <div className="cc-row">
                <span className="cc-label">FIRMWARE</span>
                <span className="cc-value">{device.firmwareVersion ?? '—'}</span>
              </div>
            </div>

            {/* Template Assignment */}
            <div className="cc-section">
              <div className="cc-section-title">TEMPLATE ASSIGNMENT</div>

              <div className="cc-field">
                <label className="cc-label">ACTIVE TEMPLATE</label>
                <div className="cc-field-row">
                  <select
                    className="cc-select"
                    value={selectedTemplateId}
                    onChange={e => setTemplateId(e.target.value)}
                  >
                    <option value="">— None —</option>
                    {templates.map(t => (
                      <option key={t.id} value={t.id}>{t.name}</option>
                    ))}
                  </select>
                  {tmplDirty && (
                    <button
                      className="cc-btn cc-btn-save"
                      onClick={handleSaveTemplate}
                    >
                      APPLY
                    </button>
                  )}
                </div>
              </div>
            </div>

          </div>

          {/* Right: Network monitoring */}
          <div className="cc-right">

            <div className="cc-section cc-section-full">
              <div className="cc-section-title">NETWORK MONITORING</div>

              <div className="cc-row">
                <span className="cc-label">WIFI SIGNAL</span>
                <span className="cc-value">
                  {liveRssi != null ? `${liveRssi} dBm — ${rssiLabel(liveRssi)}` : '—'}
                </span>
              </div>

              <div className="cc-row">
                <span className="cc-label">UPTIME</span>
                <span className="cc-value cc-online">{formatUptime(liveUptime)}</span>
              </div>

              <div className="cc-row">
                <span className="cc-label">LAST SEEN</span>
                <span className="cc-value cc-dim">
                  {device.lastSeenAt
                    ? new Date(device.lastSeenAt).toLocaleTimeString()
                    : '—'}
                </span>
              </div>

              <div className="cc-field">
                <label className="cc-label">TOKEN</label>
                <div className="cc-field-row">
                  <input
                    className="cc-input cc-input-mono"
                    value={tokenInput}
                    onChange={e => setTokenInput(e.target.value)}
                    spellCheck={false}
                    placeholder="32-char hex (no dashes)"
                    title="32-character hex token (GUID N format)"
                  />
                  <button
                    className="cc-copy-btn"
                    onClick={() => {
                      if (navigator.clipboard) {
                        navigator.clipboard.writeText(tokenInput)
                      } else {
                        const ta = document.createElement('textarea')
                        ta.value = tokenInput
                        ta.style.position = 'fixed'
                        ta.style.opacity = '0'
                        document.body.appendChild(ta)
                        ta.focus()
                        ta.select()
                        document.execCommand('copy')
                        document.body.removeChild(ta)
                      }
                    }}
                    title="Copy token"
                  >COPY</button>
                  {tokenInput.trim() !== (device.token ?? '') && (
                    <button
                      className="cc-btn cc-btn-save"
                      onClick={handleSaveToken}
                      disabled={tokenSaving}
                    >
                      {tokenStatus === 'saved' ? '✓' : tokenStatus === 'error' ? '✗' : 'SAVE'}
                    </button>
                  )}
                </div>
              </div>

              <div className="cc-chart-section">
                <div className="cc-chart-label">PING HISTORY (last {pings.length} records)</div>
                <div className="cc-chart-wrap">
                  <MiniLineChart pings={pings} maxDisplay={60} />
                </div>
                {pings.length > 0 && (() => {
                  const successCount = pings.filter(p => p.success).length
                  const ratio = (successCount / pings.length * 100).toFixed(1)
                  return (
                    <div className="cc-chart-stats">
                      <span className="cc-online">✓ {successCount} success</span>
                      <span className="cc-offline">✗ {pings.length - successCount} failed</span>
                      <span className="cc-label">{ratio}% uptime</span>
                    </div>
                  )
                })()}
              </div>
            </div>

          </div>
        </div>

        {/* Footer actions */}
        <div className="cc-actions">
          <button
            className="cc-btn cc-btn-restart"
            onClick={handleRestart}
            disabled={restarting}
            title="Send restart command to device"
          >
            {restarting ? 'RESTARTING...' : '↺ RESTART'}
          </button>
          {device.token && (
            <button
              className="cc-btn cc-btn-reburn"
              onClick={() => setReburnOpen(true)}
              title="Reflash firmware keeping the existing token and template"
            >
              ↺ REBURN
            </button>
          )}
          <button
            className="cc-btn cc-btn-terminal"
            onClick={() => setTermOpen(true)}
            title="Open a serial terminal to (re)send Wi-Fi / token over USB"
          >
            ⌨ TERMINAL
          </button>
          <span className="cc-spacer" />
          <button className="cc-btn cc-btn-cancel" onClick={onClose}>✕ CANCEL</button>
        </div>

      </div>
    </div>

    {reburnOpen && createPortal(
      <BurnModal
        isReflash
        prefillToken={device.token}
        prefillName={savedName}
        onClose={() => setReburnOpen(false)}
      />,
      document.body
    )}

    {termOpen && createPortal(
      <SerialTerminalModal
        prefillToken={device.token}
        deviceName={savedName}
        onClose={() => setTermOpen(false)}
      />,
      document.body
    )}
    </>
  )
}

export default ControllerConfigModal
