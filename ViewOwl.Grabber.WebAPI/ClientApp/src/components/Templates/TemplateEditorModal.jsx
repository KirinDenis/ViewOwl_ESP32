import { useState, useEffect } from 'react'
import CodeMirror from '@uiw/react-codemirror'
import { html } from '@codemirror/lang-html'
import { authFetch } from '../../utils/auth'
import { parseVowMeta, injectVowFrame } from '../../utils/vowMeta'
import './TemplateEditorModal.css'

function TemplateEditorModal({ template, onSave, onGrab, onClose }) {
  const [content, setContent]           = useState(template.htmlContent)
  const [previewContent, setPreviewContent] = useState(template.htmlContent)
  const [name, setName]                 = useState(template.name)
  const [saving, setSaving]             = useState(false)
  const [saveStatus, setSaveStatus]     = useState(null)
  const [starters, setStarters]         = useState([])
  const [frameIdx, setFrameIdx]         = useState(0)

  // Derived metadata — re-computed whenever the editor content changes
  const meta     = parseVowMeta(content)
  const isClassC = meta.vowClass === 'C'

  // When the frame slider or content changes for Class C, update the preview
  useEffect(() => {
    if (isClassC) {
      setPreviewContent(injectVowFrame(content, frameIdx))
    }
  }, [frameIdx, isClassC])

  // Reset frame index when switching between templates
  useEffect(() => {
    setFrameIdx(0)
  }, [template.id])

  useEffect(() => {
    authFetch('/api/templates/starters')
      .then(r => r?.json())
      .then(d => { if (Array.isArray(d)) setStarters(d) })
      .catch(() => {})
  }, [])

  const handleLoadStarter = (filename) => {
    fetch(`/SiteTemplate?name=${encodeURIComponent(filename)}`)
      .then(r => r.text())
      .then(text => {
        setContent(text)
        setPreviewContent(text)
        setFrameIdx(0)
      })
      .catch(() => {})
  }

  const starterLabel = (filename) =>
    filename.replace(/^lw-/, '').replace(/\.html$/, '').toUpperCase()

  const isDirty = content !== template.htmlContent || name !== template.name

  const handleRun = () => {
    if (isClassC) {
      setPreviewContent(injectVowFrame(content, frameIdx))
    } else {
      setPreviewContent(content)
    }
  }

  const handleSave = async () => {
    setSaving(true)
    setSaveStatus('saving')
    try {
      await onSave(template.id, name, content)
      setSaveStatus('saved')
      setTimeout(() => setSaveStatus(null), 2000)
    } catch {
      setSaveStatus('error')
      setTimeout(() => setSaveStatus(null), 3000)
    } finally {
      setSaving(false)
    }
  }

  const handleSaveAndGrab = async () => {
    await handleSave()
    await onGrab(template.id)
  }

  const handleReset = () => {
    setContent(template.htmlContent)
    setPreviewContent(template.htmlContent)
    setName(template.name)
    setFrameIdx(0)
  }

  useEffect(() => {
    const onKey = (e) => {
      if (e.key === 'Escape') onClose()
      if ((e.ctrlKey || e.metaKey) && e.key === 's') { e.preventDefault(); handleSave() }
      if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') { e.preventDefault(); handleRun() }
      // Left/Right arrow — navigate frames when Class C
      if (isClassC && e.key === 'ArrowLeft')  setFrameIdx(i => Math.max(0, i - 1))
      if (isClassC && e.key === 'ArrowRight') setFrameIdx(i => Math.min(meta.frameCount - 1, i + 1))
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  })

  const saveBtnLabel = saveStatus === 'saving' ? 'SAVING...'
    : saveStatus === 'saved'  ? '✓ SAVED'
    : saveStatus === 'error'  ? '✗ ERROR'
    : '💾 SAVE'

  const previewLabel = isClassC
    ? `CLASS C ANIMATION · ${meta.frameCount} FRAMES · ${meta.fps} FPS`
    : 'LIVE PREVIEW — 480 × 320 px'

  return (
    <div className="editor-overlay" onClick={(e) => { if (e.target === e.currentTarget) onClose() }}>
      <div className="editor-modal hud-panel">

        {/* Header */}
        <div className="editor-header">
          <div className="editor-header-left">
            <span className="editor-label">TEMPLATE EDITOR</span>
            {isClassC && (
              <span className="editor-class-c-pill">◈ CLASS C</span>
            )}
            <input
              className="editor-name-input"
              value={name}
              onChange={e => setName(e.target.value)}
              spellCheck={false}
              title="Template name"
            />
            {isDirty && <span className="editor-dirty" title="Unsaved changes">●</span>}
          </div>
          <button className="editor-close-btn" onClick={onClose} title="Close (Esc)">✕</button>
        </div>

        {/* Body: Editor + Preview */}
        <div className="editor-body">
          <div className="editor-pane">
            <div className="pane-label">HTML / CSS / JS</div>
            <div className="editor-cm-wrap">
              <CodeMirror
                value={content}
                height="100%"
                theme="dark"
                extensions={[html()]}
                onChange={(val) => setContent(val)}
                basicSetup={{
                  lineNumbers: true,
                  foldGutter: true,
                  bracketMatching: true,
                  autocompletion: true,
                  highlightActiveLine: true
                }}
              />
            </div>
          </div>

          <div className="preview-pane">
            <div className="pane-label">{previewLabel}</div>
            <div className="preview-frame-wrap">
              <iframe
                className="preview-iframe"
                srcDoc={previewContent}
                width="480"
                height="320"
                sandbox="allow-scripts"
                title="Template preview"
              />
            </div>

            {/* Frame navigator — shown only for Class C */}
            {isClassC && (
              <div className="frame-navigator">
                <button
                  className="frame-nav-btn"
                  onClick={() => setFrameIdx(i => Math.max(0, i - 1))}
                  disabled={frameIdx === 0}
                  title="Previous frame (←)"
                >◀</button>
                <div className="frame-nav-info">
                  <span className="frame-nav-label">FRAME</span>
                  <span className="frame-nav-index">{frameIdx + 1} / {meta.frameCount}</span>
                  <span className="frame-nav-fps">{meta.fps} FPS</span>
                </div>
                {/* Clickable frame dots */}
                <div className="frame-dots">
                  {Array.from({ length: meta.frameCount }, (_, i) => (
                    <button
                      key={i}
                      className={`frame-dot${i === frameIdx ? ' active' : ''}`}
                      onClick={() => setFrameIdx(i)}
                      title={`Frame ${i + 1}`}
                    />
                  ))}
                </div>
                <button
                  className="frame-nav-btn"
                  onClick={() => setFrameIdx(i => Math.min(meta.frameCount - 1, i + 1))}
                  disabled={frameIdx === meta.frameCount - 1}
                  title="Next frame (→)"
                >▶</button>
              </div>
            )}

            <div className="preview-shortcuts">
              <span>Ctrl+Enter — Run</span>
              <span>Ctrl+S — Save</span>
              {isClassC && <span>← → — Frames</span>}
              <span>Esc — Close</span>
            </div>

            {starters.length > 0 && (
              <div className="starters-panel">
                <div className="starters-label">◈ STARTER TEMPLATES</div>
                <div className="starters-list">
                  {starters.map(s => (
                    <button
                      key={s}
                      className="starter-item"
                      onClick={() => handleLoadStarter(s)}
                      title={`Load ${s}`}
                    >
                      {starterLabel(s)}
                    </button>
                  ))}
                </div>
              </div>
            )}
          </div>
        </div>

        {/* Footer actions */}
        <div className="editor-actions">
          <button className="ed-btn ed-btn-run" onClick={handleRun} title="Ctrl+Enter">
            ▶ RUN
          </button>
          <button
            className={`ed-btn ed-btn-save ${saveStatus === 'saved' ? 'status-ok' : saveStatus === 'error' ? 'status-err' : ''}`}
            onClick={handleSave}
            disabled={saving}
            title="Ctrl+S"
          >
            {saveBtnLabel}
          </button>
          <button className="ed-btn ed-btn-grab" onClick={handleSaveAndGrab} title="Save then trigger screenshot grab">
            📷 SAVE &amp; GRAB
          </button>
          <button className="ed-btn ed-btn-reset" onClick={handleReset} title="Revert all changes">
            ↺ RESET
          </button>
          <span className="ed-spacer" />
          <button className="ed-btn ed-btn-cancel" onClick={onClose}>
            CANCEL
          </button>
        </div>

      </div>
    </div>
  )
}
export default TemplateEditorModal
