import { useState, useEffect, useRef } from 'react'
import StatusLED from '../HUD/StatusLED'
import { authFetch } from '../../utils/auth'
import { parseVowMeta, injectVowFrame } from '../../utils/vowMeta'
import { useAuth } from '../../utils/AuthContext'
import './TemplateCard.css'

function TemplateCard({ template, onEdit, onDelete, grabEvent, onMetaLoaded }) {
  const { isAdmin } = useAuth()
  const [stats, setStats]             = useState(null)
  const [htmlContent, setHtmlContent] = useState(null)
  const [previewUrl, setPreviewUrl]   = useState(null)
  const [hasScreenshot, setHasScreenshot] = useState(false)
  const [viewMode, setViewMode]       = useState('live')
  const [scale, setScale]             = useState(0.46)
  const [meta, setMeta]               = useState(null)
  const previewRef                    = useRef(null)
  const blobUrlRef                    = useRef(null)

  const loadStats = () => {
    authFetch(`/api/templates/${template.id}/stats`)
      .then(r => r?.json())
      .then(d => { if (d) setStats(d) })
      .catch(() => {})
  }

  const loadContent = () => {
    authFetch(`/api/templates/${template.id}`)
      .then(r => r?.json())
      .then(d => {
        if (!d?.htmlContent) return
        setHtmlContent(d.htmlContent)
        const m = parseVowMeta(d.htmlContent)
        setMeta(m)
        onMetaLoaded?.(template.id, m)
      })
      .catch(() => {})
  }

  const loadScreenshot = () => {
    authFetch(`/api/templates/${template.id}/preview-image`)
      .then(r => {
        if (!r?.ok) { setHasScreenshot(false); return null }
        setHasScreenshot(true)
        return r.blob()
      })
      .then(blob => {
        if (!blob) return
        if (blobUrlRef.current) URL.revokeObjectURL(blobUrlRef.current)
        const url = URL.createObjectURL(blob)
        blobUrlRef.current = url
        setPreviewUrl(url)
      })
      .catch(() => setHasScreenshot(false))
  }

  useEffect(() => {
    loadStats()
    loadContent()
    loadScreenshot()
    return () => { if (blobUrlRef.current) URL.revokeObjectURL(blobUrlRef.current) }
  }, [template.id])

  useEffect(() => {
    if (grabEvent?.templateId === template.id && grabEvent.success) {
      loadStats()
      loadContent()
      loadScreenshot()
    }
  }, [grabEvent])

  useEffect(() => {
    const el = previewRef.current
    if (!el) return
    const ro = new ResizeObserver(([entry]) => {
      setScale(entry.contentRect.width / 480)
    })
    ro.observe(el)
    return () => ro.disconnect()
  }, [])

  const formatRelative = (dateStr) => {
    if (!dateStr) return 'Never'
    const diff = Date.now() - new Date(dateStr).getTime()
    const min = Math.floor(diff / 60000)
    if (min < 1) return 'Just now'
    if (min < 60) return `${min}m ago`
    const h = Math.floor(min / 60)
    if (h < 24) return `${h}h ago`
    return `${Math.floor(h / 24)}d ago`
  }

  const isActive    = (stats?.grabCount ?? 0) > 0
  const isClassC    = meta?.vowClass === 'C'
  const category    = meta?.category ?? ''
  // For Class C cards, render frame 0 in the live preview using srcDoc injection
  const liveContent = isClassC && htmlContent
    ? injectVowFrame(htmlContent, 0)
    : htmlContent

  return (
    <div className="template-card hud-panel">

      {/* Header */}
      <div className="template-header">
        <span className="template-name glow-text">{template.name}</span>
        <div className="template-header-right">
          {/* Category tag */}
          {category && category !== 'other' && (
            <span className="vow-category-tag">{category.toUpperCase()}</span>
          )}
          {/* Class C animation badge */}
          {isClassC && (
            <span className="vow-class-c-badge" title={`${meta.frameCount} frames · ${meta.fps} fps`}>
              ◈ ANIM {meta.frameCount}F
            </span>
          )}
          {/* View mode toggle */}
          <div className="template-view-toggle">
            <button
              className={`toggle-btn${viewMode === 'live' ? ' active' : ''}`}
              onClick={e => { e.stopPropagation(); setViewMode('live') }}
              title="Live HTML preview"
            >HTML</button>
            <button
              className={`toggle-btn${viewMode === 'screenshot' ? ' active' : ''}`}
              onClick={e => { e.stopPropagation(); setViewMode('screenshot') }}
              title="Last screenshot"
            >IMG</button>
          </div>
        </div>
      </div>

      {/* Preview — click to edit only for admins */}
      <div className="template-preview" ref={previewRef} onClick={isAdmin ? onEdit : undefined}
        style={{ cursor: isAdmin ? 'pointer' : 'default' }}>
        {viewMode === 'live'
          ? liveContent
            ? (
              <iframe
                srcDoc={liveContent}
                style={{
                  width: '480px',
                  height: '320px',
                  border: 'none',
                  transform: `scale(${scale})`,
                  transformOrigin: '0 0',
                  pointerEvents: 'none',
                  display: 'block'
                }}
                sandbox="allow-scripts"
                title={template.name}
              />
            )
            : (
              <div className="template-preview-placeholder">
                <span className="preview-loading">LOADING...</span>
              </div>
            )
          : previewUrl
            ? <img src={previewUrl} alt={template.name} />
            : (
              <div className="template-preview-placeholder">
                <span className="preview-no-grab">NO SCREENSHOT YET</span>
                <span className="preview-hint">CLICK EDIT → GRAB</span>
              </div>
            )
        }
        {isAdmin && <div className="template-preview-overlay">EDIT</div>}
      </div>

      {/* Stats */}
      <div className="template-stats">
        <div className="stat-row">
          <StatusLED status={isActive ? 'online' : 'offline'} />
          <span className="stat-label">Grabs</span>
          <span className="stat-value">{stats?.grabCount ?? '—'}</span>
        </div>
        <div className="stat-row">
          <StatusLED status={(stats?.deliveryCount ?? 0) > 0 ? 'online' : 'offline'} />
          <span className="stat-label">Sent</span>
          <span className="stat-value">{stats?.deliveryCount ?? '—'}</span>
        </div>
        <div className="stat-row-last">
          <span className="stat-label">Last</span>
          <span className="stat-value-dim">{formatRelative(stats?.lastGrabbed)}</span>
        </div>
      </div>

      {isAdmin && (
        <div className="template-action-row">
          <button className="template-edit-btn" onClick={onEdit}>▸ EDIT</button>
          <button className="template-delete-btn" onClick={onDelete} title="Delete template">✕</button>
        </div>
      )}
    </div>
  )
}
export default TemplateCard
