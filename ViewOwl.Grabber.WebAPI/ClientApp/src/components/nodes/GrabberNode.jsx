import { useState, useEffect, useCallback, useRef, memo } from 'react'
import { createPortal } from 'react-dom'
import { Handle, Position, useReactFlow } from 'reactflow'
import { authFetch } from '../../utils/auth'
import TemplateEditorModal from '../Templates/TemplateEditorModal'
import './GrabberNode.css'

// BGR565 little-endian: bits[4:0]=B, bits[10:5]=G, bits[15:11]=R
function rgbaToBgr565(rgba, width, height) {
  const pixels = width * height
  const out = new Uint8Array(pixels * 2)
  for (let i = 0; i < pixels; i++) {
    const r = rgba[i * 4]
    const g = rgba[i * 4 + 1]
    const b = rgba[i * 4 + 2]
    const v = ((b >> 3) & 0x1F) | (((g >> 2) & 0x3F) << 5) | (((r >> 3) & 0x1F) << 11)
    out[i * 2]     = v & 0xFF
    out[i * 2 + 1] = (v >> 8) & 0xFF
  }
  return out
}

// Reduces BGR565 color precision iteratively until unique count ≤ 254.
// Tries shift=1 (≤8 K unique), shift=2 (≤1 K unique), shift=3 (≤128, guaranteed).
// Applied only when the palette overflows, so simple frames are never touched.
function quantizeBgr565Array(bgr565) {
  for (let shift = 1; shift <= 3; shift++) {
    const out  = new Uint8Array(bgr565.length)
    const seen = new Set()
    for (let i = 0; i < bgr565.length; i += 2) {
      const raw = bgr565[i] | (bgr565[i + 1] << 8)
      const b   = ((raw        & 0x1F) >> shift) << shift
      const g   = (((raw >> 5) & 0x3F) >> shift) << shift
      const r   = (((raw >>11) & 0x1F) >> shift) << shift
      const q   = b | (g << 5) | (r << 11)
      out[i]     = q & 0xFF
      out[i + 1] = q >> 8
      seen.add(q)
    }
    if (seen.size <= 254) return out
  }
  return bgr565  // shift=3 always yields ≤ 128 unique colors — unreachable
}

// Palette+RLE compression matching server-side FrameCompressor.CompressRle() (FRAME_FLAG_PALETTE=0x01).
// Format: [0x01][palLen:u8][palette: palLen×2 bytes big-endian][RLE: count:u8, idx:u8 pairs]
// Quantizes color precision when unique colors exceed 255 so palette mode always fits.
// Pre-allocates worst-case buffers to avoid GC churn on the hot path.
function paletteRleCompress(bgr565, pixelCount) {

  // Pass 1: build palette (max 255 distinct colors)
  const paletteMap = new Map()        // 16-bit color value → palette index
  const palette    = new Uint16Array(255)
  let   palLen     = 0

  for (let i = 0; i < pixelCount; i++) {
    // Little-endian BGR565 bytes → 16-bit color value
    const color = bgr565[i * 2] | (bgr565[i * 2 + 1] << 8)
    if (!paletteMap.has(color)) {
      if (palLen >= 255) {
        // Too many unique colors — reduce color precision and retry.
        // quantizeBgr565Array guarantees ≤ 254 unique colors so the recursive
        // call will not overflow the palette again.
        return paletteRleCompress(quantizeBgr565Array(bgr565), pixelCount)
      }
      paletteMap.set(color, palLen)
      palette[palLen++] = color
    }
  }

  // Pass 2: RLE-encode palette indices — worst case all unique → pixelCount pairs
  const rleBuf = new Uint8Array(pixelCount * 2)
  let rleLen = 0

  let i = 0
  while (i < pixelCount) {
    const color = bgr565[i * 2] | (bgr565[i * 2 + 1] << 8)
    const idx   = paletteMap.get(color)
    let run = 1
    while (run < 255 && i + run < pixelCount) {
      const nc = bgr565[(i + run) * 2] | (bgr565[(i + run) * 2 + 1] << 8)
      if (nc !== color) break
      run++
    }
    rleBuf[rleLen++] = run
    rleBuf[rleLen++] = idx
    i += run
  }

  // Assemble: flag(1) + palLen(1) + palette(N×2 big-endian) + RLE stream
  const out = new Uint8Array(2 + palLen * 2 + rleLen)
  let pos = 0
  out[pos++] = 0x01  // FRAME_FLAG_PALETTE
  out[pos++] = palLen
  for (let p = 0; p < palLen; p++) {
    const c = palette[p]
    out[pos++] = (c >> 8) & 0xFF  // big-endian high byte first (matches server)
    out[pos++] =  c       & 0xFF
  }
  out.set(rleBuf.subarray(0, rleLen), pos)

  return out
}

// Available push intervals in seconds the user can cycle through
const INTERVAL_STEPS = [1, 2, 5]

const GrabberNode = ({ id, data }) => {
  const [htmlContent, setHtmlContent]       = useState(null)
  const [templateDetail, setTemplateDetail] = useState(null)
  const [stats, setStats]                   = useState(null)
  const [scale, setScale]                   = useState(250 / 480)
  const [pixelPerfect, setPixelPerfect]     = useState(false) // true → scale locked to device resolution
  const [editOpen, setEditOpen]             = useState(false)
  const [viewMode, setViewMode]             = useState('live')
  const [previewUrl, setPreviewUrl]         = useState(null)
  const [grabError, setGrabError]           = useState(null)

  // Browser push state — persisted per template so it survives page refresh
  const [pushActive, setPushActive]         = useState(() => localStorage.getItem(`vow_push_${data.id}`) === '1')
  const [pushInterval, setPushInterval]     = useState(() => {
    const v = parseInt(localStorage.getItem(`vow_push_iv_${data.id}`), 10)
    return INTERVAL_STEPS.includes(v) ? v : 2
  })
  const [pushActualFps, setPushActualFps]   = useState(null)  // measured fps
  const [pushError, setPushError]           = useState(null)  // last push error

  const { setNodes } = useReactFlow()

  const previewRef  = useRef(null)
  const grabRef     = useRef(null)  // ref to the hidden grab iframe
  const blobUrlRef  = useRef(null)

  // Push loop internals
  const pushTimerRef    = useRef(null)
  const pushCountRef    = useRef(0)   // frames pushed in current second
  const pushSecTimerRef = useRef(null) // 1-second fps measurement tick

  // Fetch full template detail (name + htmlContent)
  useEffect(() => {
    authFetch(`/api/templates/${data.id}`)
      .then(r => r?.json())
      .then(d => {
        if (!d) return
        setHtmlContent(d.htmlContent)
        setTemplateDetail(d)
      })
      .catch(() => {})
  }, [data.id])

  // Fetch stats; also called on grab events for this template
  const loadStats = useCallback(() => {
    authFetch(`/api/templates/${data.id}/stats`)
      .then(r => r?.json())
      .then(d => { if (d) setStats(d) })
      .catch(() => {})
  }, [data.id])

  const loadScreenshot = useCallback(() => {
    authFetch(`/api/templates/${data.id}/preview-image`)
      .then(r => {
        if (!r?.ok) return null
        return r.blob()
      })
      .then(blob => {
        if (!blob) return
        if (blobUrlRef.current) URL.revokeObjectURL(blobUrlRef.current)
        const url = URL.createObjectURL(blob)
        blobUrlRef.current = url
        setPreviewUrl(url)
      })
      .catch(() => {})
  }, [data.id])

  useEffect(() => {
    loadStats()
    loadScreenshot()
    return () => { if (blobUrlRef.current) URL.revokeObjectURL(blobUrlRef.current) }
  }, [loadStats, loadScreenshot])

  // Auto-start push for freshly created templates (< 60 s old) — once per browser session.
  // This fires when a new device card is added so the user sees it live immediately.
  useEffect(() => {
    if (!data.createdAt) return
    const ageMs = Date.now() - new Date(data.createdAt).getTime()
    if (ageMs > 60000) return
    const key = `viewowl:push-auto-${data.id}`
    if (sessionStorage.getItem(key)) return
    sessionStorage.setItem(key, '1')
    setPushActive(true)
  }, []) // eslint-disable-line react-hooks/exhaustive-deps — intentionally once on mount

  // Live-refresh stats and screenshot when a server-side grab completes.
  useEffect(() => {
    const evt = data.grabEvent
    if (!evt) return
    const id = evt.templateId ?? evt.id
    if (id !== data.id) return
    loadStats()
    if (evt.success) {
      loadScreenshot()
      setGrabError(null)
    } else {
      setGrabError(evt.errorMessage ?? evt.message ?? 'grab failed')
    }
  }, [data.grabEvent, data.id, loadStats, loadScreenshot])

  // Persist push state across page refreshes
  useEffect(() => { localStorage.setItem(`vow_push_${data.id}`, pushActive ? '1' : '0') }, [pushActive, data.id])
  useEffect(() => { localStorage.setItem(`vow_push_iv_${data.id}`, pushInterval) }, [pushInterval, data.id])

  // Responsive preview scale — disabled when pixel-perfect mode is active
  useEffect(() => {
    if (pixelPerfect) {
      // Lock scale to actual device resolution ratio
      setScale((data.pushWidth ?? 480) / 480)
      return
    }
    const el = previewRef.current
    if (!el) return
    const ro = new ResizeObserver(([entry]) => {
      setScale(entry.contentRect.width / 480)
    })
    ro.observe(el)
    return () => ro.disconnect()
  }, [pixelPerfect, data.pushWidth])

  // ── Browser push loop ──────────────────────────────────────────────────────

  // Grab one frame: read pixels from hidden iframe canvas → convert → POST
  const doGrab = useCallback(async () => {
    const iframe = grabRef.current
    if (!iframe) return

    let imageData
    try {
      const doc = iframe.contentDocument
      if (!doc) return

      // Try standard canvas#screen first, then any canvas in the document
      const canvas = doc.getElementById('screen') ?? doc.querySelector('canvas')
      if (!canvas) {
        setPushError('No <canvas> in template — DOM-only templates not supported')
        return  // show warning but keep push active; user can switch to a canvas template
      }

      const ctx = canvas.getContext('2d')
      if (!ctx) return

      const targetW = data.pushWidth  ?? 480
      const targetH = data.pushHeight ?? 320

      if (canvas.width === targetW && canvas.height === targetH) {
        // Canvas already matches device resolution — grab directly
        imageData = ctx.getImageData(0, 0, targetW, targetH)
      } else {
        // Template canvas is a different size (e.g. 480×320 for a 320×240 device).
        // Scale to device resolution via an offscreen canvas so the full frame is
        // visible rather than cropping the top-left corner.
        const off = document.createElement('canvas')
        off.width  = targetW
        off.height = targetH
        const offCtx = off.getContext('2d')
        offCtx.drawImage(canvas, 0, 0, targetW, targetH)
        imageData = offCtx.getImageData(0, 0, targetW, targetH)
      }

      setPushError(null)  // clear any previous canvas-not-found warning
    } catch {
      // iframe not ready yet — skip this frame silently
      return
    }

    const w          = data.pushWidth  ?? 480
    const h          = data.pushHeight ?? 320
    const bgr565     = rgbaToBgr565(imageData.data, w, h)
    const frameBytes = paletteRleCompress(bgr565, w * h)  // always compress: raw ≤ 307 KB, ESP32 buf=108 KB

    try {
      const res = await authFetch(`/api/templates/${data.id}/frame?w=${w}&h=${h}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/octet-stream' },
        body: frameBytes.buffer.slice(frameBytes.byteOffset, frameBytes.byteOffset + frameBytes.byteLength),
      })
      if (res?.status === 429) {
        // Server-side rate limit hit — browser is faster than the ceiling, just skip
        return
      }
      if (res?.status === 422) {
        // No server-side grab exists yet — wait for the worker to complete the first grab
        setPushError('Waiting for first server grab… click ↺ to trigger one')
        return  // keep push active; it will retry on next interval
      }
      if (!res?.ok) {
        setPushError(`server error ${res?.status} — push stopped`)
        setPushActive(false)
        return
      }
      setPushError(null)
      pushCountRef.current++
    } catch {
      // Network error — keep trying
    }
  }, [data.id, data.pushWidth, data.pushHeight])

  // Start / stop the push loop when pushActive or pushFps changes
  useEffect(() => {
    if (!pushActive) {
      clearInterval(pushTimerRef.current)
      clearInterval(pushSecTimerRef.current)
      pushTimerRef.current    = null
      pushSecTimerRef.current = null
      setPushActualFps(null)
      pushCountRef.current = 0
      return
    }

    const intervalMs = pushInterval * 1000
    pushTimerRef.current = setInterval(doGrab, intervalMs)

    // Measure actual fps every second
    pushSecTimerRef.current = setInterval(() => {
      setPushActualFps(pushCountRef.current)
      pushCountRef.current = 0
    }, 1000)

    return () => {
      clearInterval(pushTimerRef.current)
      clearInterval(pushSecTimerRef.current)
    }
  }, [pushActive, pushInterval, doGrab])

  // Clean up on unmount
  useEffect(() => () => {
    clearInterval(pushTimerRef.current)
    clearInterval(pushSecTimerRef.current)
  }, [])

  const formatRelative = (iso) => {
    if (!iso) return '—'
    const mins = Math.floor((Date.now() - new Date(iso).getTime()) / 60000)
    if (mins < 1)  return 'just now'
    if (mins < 60) return `${mins}m ago`
    const hrs = Math.floor(mins / 60)
    if (hrs < 24)  return `${hrs}h ago`
    return `${Math.floor(hrs / 24)}d ago`
  }

  // Save handler — updates preview and node title on success
  const handleSave = async (templateId, name, content) => {
    const res = await authFetch(`/api/templates/${templateId}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name, htmlContent: content }),
    })
    if (!res?.ok) throw new Error(`HTTP ${res?.status}`)
    setHtmlContent(content)
    setTemplateDetail(prev => prev ? { ...prev, name, htmlContent: content } : prev)
    // Immediately reflect the new name on the canvas node without a full reload.
    setNodes(nodes => nodes.map(n => n.id === id ? { ...n, data: { ...n.data, name } } : n))
  }

  const handleGrab = (id) =>
    authFetch(`/api/templates/${id}/grab`, { method: 'POST' })

  const cycleInterval = (e) => {
    e.stopPropagation()
    const idx = INTERVAL_STEPS.indexOf(pushInterval)
    setPushInterval(INTERVAL_STEPS[(idx + 1) % INTERVAL_STEPS.length])
  }

  const togglePush = (e) => {
    e.stopPropagation()
    setPushError(null)
    setPushActive(v => !v)
  }

  return (
    <>
      <div className={`grabber-node${grabError ? ' grabber-error' : ''}${pushActive ? ' push-active' : ''}`}>

        {/* Header: name + error badge + HTML/IMG toggle + close button */}
        <div className="node-header">
          <span className="node-title">{data.name}</span>
          {grabError && <span className="grab-error-badge" title={grabError}>ERR</span>}
          <div className="node-view-toggle">
            <button
              className={`node-toggle-btn${viewMode === 'live' ? ' active' : ''}`}
              onClick={e => { e.stopPropagation(); setViewMode('live') }}
              onPointerDown={e => e.stopPropagation()}
              title="Live HTML preview"
            >HTML</button>
            <button
              className={`node-toggle-btn${viewMode === 'screenshot' ? ' active' : ''}`}
              onClick={e => { e.stopPropagation(); setViewMode('screenshot') }}
              onPointerDown={e => e.stopPropagation()}
              title="Last screenshot"
            >IMG</button>
            <button
              className={`node-toggle-btn${pixelPerfect ? ' active' : ''}`}
              onClick={e => { e.stopPropagation(); setPixelPerfect(v => !v) }}
              onPointerDown={e => e.stopPropagation()}
              title={pixelPerfect ? 'Auto scale (click to disable pixel-perfect)' : `Pixel-perfect: scale to ${data.pushWidth ?? 480}×${data.pushHeight ?? 320}`}
            >1:1</button>
          </div>
          <button
            className="node-delete-btn"
            onClick={() => data.onDelete?.()}
            onPointerDown={e => e.stopPropagation()}
            title="Delete this template"
          >✕</button>
        </div>

        {/* Preview: live iframe or last screenshot */}
        <div className="node-preview" ref={previewRef} onClick={() => setEditOpen(true)}>
          {viewMode === 'live'
            ? htmlContent
              ? (
                <iframe
                  srcDoc={htmlContent}
                  style={{
                    width: '480px',
                    height: '320px',
                    border: 'none',
                    transform: `scale(${scale})`,
                    transformOrigin: '0 0',
                    pointerEvents: 'none',
                    display: 'block',
                  }}
                  sandbox="allow-scripts allow-same-origin"
                  title={`Preview ${data.name}`}
                />
              )
              : (
                <div className="node-preview-placeholder">
                  <span>LOADING...</span>
                </div>
              )
            : previewUrl
              ? <img src={previewUrl} alt={data.name} style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }} />
              : (
                <div className="node-preview-placeholder">
                  <span>NO SCREENSHOT YET</span>
                </div>
              )
          }
        </div>

        {/* Stats */}
        <div className="node-body">
          <div className="node-stat">
            <span className="stat-label">GRABS</span>
            <span className="stat-value stat-primary">{stats?.grabCount ?? '—'}</span>
          </div>
          <div className="node-stat">
            <span className="stat-label">SENT</span>
            <span className="stat-value stat-primary">{stats?.deliveryCount ?? '—'}</span>
          </div>
          <div className="node-stat">
            <span className="stat-label">LAST</span>
            <span className="stat-value stat-dim">{formatRelative(stats?.lastGrabbed)}</span>
          </div>
        </div>

        {/* Footer: edit button + push controls — delete moved to header */}
        <div className="node-footer">
          <button
            className="node-edit-btn"
            onClick={() => setEditOpen(true)}
            onPointerDown={e => e.stopPropagation()}
          >
            ▶ EDIT
          </button>

          <div className="node-push-controls">
            <button
              className={`node-push-btn${pushActive ? ' active' : ''}`}
              onClick={togglePush}
              onPointerDown={e => e.stopPropagation()}
              title={pushActive
                ? 'Browser push active — click to stop'
                : 'Browser push: grab canvas from this tab and stream directly to device'}
            >
              ⚡ {pushActive
                ? <span className="push-fps-display">
                    {pushActualFps !== null ? pushActualFps : '…'}fps
                    <span className="push-dot" />
                  </span>
                : 'PUSH'}
            </button>
            <button
              className="node-fps-btn"
              onClick={cycleInterval}
              onPointerDown={e => e.stopPropagation()}
              title="Click to change push interval"
            >
              {pushInterval}s
            </button>
          </div>
        </div>

        {/* Push error banner */}
        {pushError && (
          <div className="push-error-bar" title={pushError}>
            ⚠ {pushError}
          </div>
        )}

        <Handle type="source" position={Position.Right} id="output" className="handle-output" />
      </div>

      {/* Hidden grab iframe — portalled to document.body so it escapes the
          React Flow node's CSS transform context.  position:fixed inside a
          transformed parent is no longer viewport-fixed (CSS spec §9.7), which
          caused the iframe to expand the node and show a white area.
          Portalling to body gives true viewport positioning.
          opacity:0 (not visibility:hidden) keeps Chrome's requestAnimationFrame
          running at 60 fps so the canvas is fully initialised.
          Mounted as soon as htmlContent is available (not gated on pushActive)
          so the template JS has time to reach its steady-state render before
          the first doGrab fires.  Gating on pushActive caused the iframe to
          mount fresh on every push-start, capturing the initial white canvas
          instead of the live animated state. */}
      {htmlContent && createPortal(
        <iframe
          ref={grabRef}
          srcDoc={htmlContent}
          sandbox="allow-scripts allow-same-origin"
          title="grab-worker"
          style={{
            position: 'fixed',
            width:  `${data.pushWidth ?? 480}px`,
            height: `${data.pushHeight ?? 320}px`,
            left:   '0px',
            top:    '0px',
            opacity: 0,
            zIndex: -9999,
            pointerEvents: 'none',
          }}
        />,
        document.body
      )}

      {editOpen && templateDetail && createPortal(
        <TemplateEditorModal
          template={templateDetail}
          onSave={handleSave}
          onGrab={handleGrab}
          onClose={() => setEditOpen(false)}
        />,
        document.body
      )}
    </>
  )
}

export default memo(GrabberNode)
