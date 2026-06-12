import { useState, useEffect, useCallback, useRef } from 'react'
import CodeMirror from '@uiw/react-codemirror'
import { html } from '@codemirror/lang-html'
import { vscodeDark } from '@uiw/codemirror-theme-vscode'
import { authFetch } from '../../utils/auth'
import { confirm } from '../../utils/confirm'
import { parseVowMeta, injectVowFrame } from '../../utils/vowMeta'
import './TemplateManagerModal.css'

// ── Constants ────────────────────────────────────────────────────────────────

const CLASS_FILTERS = [
  { value: 'all', label: 'ALL' },
  { value: 'A',   label: 'A' },
  { value: 'B',   label: 'B' },
  { value: 'C',   label: 'C' },
]

// ── Helpers ───────────────────────────────────────────────────────────────────

/**
 * Injects or replaces a data-vow-{attr} value inside an HTML string.
 * Tries in order: replace existing attribute → inject into <body> → append at end.
 */
function setVowAttr(html, attr, value) {
  const re = new RegExp(`data-vow-${attr}="[^"]*"`, 'i')
  if (re.test(html)) {
    return html.replace(re, `data-vow-${attr}="${value}"`)
  }
  // Inject before the closing > of the <body> opening tag
  if (/<body[\s>]/i.test(html)) {
    return html.replace(/(<body[^>]*?)(\s*>)/i, `$1\n    data-vow-${attr}="${value}"$2`)
  }
  // No <body> — no-op (template is probably being started fresh)
  return html
}

// ── Component ────────────────────────────────────────────────────────────────

function TemplateManagerModal({ onClose }) {
  // ── Tabs ──────────────────────────────────────────────────────────────────
  const [tab, setTab] = useState('templates')

  // ── Template list state ───────────────────────────────────────────────────
  const [templates, setTemplates]         = useState([])
  const [loadingList, setLoadingList]     = useState(true)
  const [searchQuery, setSearchQuery]     = useState('')
  const [classFilter, setClassFilter]     = useState('all')
  const [catFilter, setCatFilter]         = useState('all')
  // metaMap: templateId → VowMeta (populated when detail is loaded)
  const [metaMap, setMetaMap]             = useState({})

  // ── Editor state ──────────────────────────────────────────────────────────
  const [selectedId, setSelectedId]       = useState(null)
  const [editorName, setEditorName]       = useState('')
  const [editorContent, setEditorContent] = useState('')
  const [originalName, setOriginalName]   = useState('')
  const [originalContent, setOriginalContent] = useState('')
  const [saving, setSaving]               = useState(false)
  const [saveStatus, setSaveStatus]       = useState(null)
  const [pinStatus, setPinStatus]         = useState(null)   // null | 'added' | 'already'
  const [isPublic, setIsPublic]           = useState(false)  // landing page visibility flag
  const [publicStatus, setPublicStatus]   = useState(null)   // null | 'saving' | 'ok' | 'err'
  const [frameIdx, setFrameIdx]           = useState(0)
  const [previewContent, setPreviewContent] = useState('')
  const [loadingDetail, setLoadingDetail] = useState(false)
  const isNewRef                          = useRef(false)
  // Tracks whether any save or delete occurred so App.jsx only reloads when needed.
  const changedRef                        = useRef(false)

  // ── Auto-play state (Class C) ─────────────────────────────────────────────
  const [isPlaying, setIsPlaying]         = useState(false)
  const playTimerRef                      = useRef(null)

  // ── Category tab state ────────────────────────────────────────────────────
  const [categories, setCategories]       = useState([])
  const [catLoading, setCatLoading]       = useState(true)
  const [editingCatId, setEditingCatId]   = useState(null)
  const [editingCatLabel, setEditingCatLabel] = useState('')
  const [newCatLabel, setNewCatLabel]     = useState('')
  const [catAddError, setCatAddError]     = useState(null)  // null | string
  const [catAddBusy, setCatAddBusy]       = useState(false)

  // ── List keyboard ref ─────────────────────────────────────────────────────
  const listRef = useRef(null)

  // ── Derived ───────────────────────────────────────────────────────────────
  const meta     = parseVowMeta(editorContent)
  const isClassC = meta.vowClass === 'C'
  const isDirty  = editorContent !== originalContent || editorName !== originalName

  // ── Auto-play timer ───────────────────────────────────────────────────────
  useEffect(() => {
    if (isPlaying && isClassC && meta.frameCount > 1) {
      const ms = Math.max(50, Math.round(1000 / meta.fps))
      playTimerRef.current = setInterval(() => {
        setFrameIdx(i => (i + 1) % meta.frameCount)
      }, ms)
      return () => clearInterval(playTimerRef.current)
    }
    clearInterval(playTimerRef.current)
    return undefined
  }, [isPlaying, isClassC, meta.fps, meta.frameCount])

  // Stop playing when switching templates
  useEffect(() => { setIsPlaying(false) }, [selectedId])

  // ── Load templates list ───────────────────────────────────────────────────
  const loadTemplates = useCallback(() => {
    setLoadingList(true)
    authFetch('/api/templates')
      .then(r => (r?.ok ? r.json() : null))
      .then(data => {
        if (Array.isArray(data)) {
          setTemplates(data)
          // Pre-populate metaMap with class/category from the list response so
          // category and class filters work immediately without opening each template.
          // Don't overwrite entries already loaded from the full detail endpoint.
          setMetaMap(prev => {
            const patch = {}
            data.forEach(t => {
              if (!prev[t.id]) {
                patch[t.id] = {
                  vowClass:    t.vowClass   ?? 'A',
                  category:    t.category   ?? 'other',
                  frameCount:  1,
                  fps:         1,
                  refresh:     60,
                }
              }
            })
            return Object.keys(patch).length > 0 ? { ...prev, ...patch } : prev
          })
        }
        setLoadingList(false)
      })
      .catch(() => setLoadingList(false))
  }, [])

  // ── Load categories ───────────────────────────────────────────────────────
  const loadCategories = useCallback(() => {
    setCatLoading(true)
    authFetch('/api/template-categories')
      .then(r => (r?.ok ? r.json() : null))
      .then(data => {
        if (Array.isArray(data)) setCategories(data)
        setCatLoading(false)
      })
      .catch(() => setCatLoading(false))
  }, [])

  useEffect(() => {
    loadTemplates()
    loadCategories()
  }, [loadTemplates, loadCategories])

  // ── Class C preview sync ─────────────────────────────────────────────────
  useEffect(() => {
    if (isClassC) {
      setPreviewContent(injectVowFrame(editorContent, frameIdx))
    }
  }, [frameIdx, isClassC, editorContent])

  // ── Select template → load full detail ───────────────────────────────────
  const selectTemplate = useCallback(async (id) => {
    if (isDirty) {
      if (!await confirm('You have unsaved changes. Discard them?', { title: 'UNSAVED CHANGES' })) return
    }
    setSelectedId(id)
    setLoadingDetail(true)
    setFrameIdx(0)
    setIsPlaying(false)
    isNewRef.current = false
    authFetch(`/api/templates/${id}`)
      .then(r => (r?.ok ? r.json() : null))
      .then(detail => {
        if (!detail) return
        const content = detail.htmlContent ?? ''
        const m = parseVowMeta(content)
        setMetaMap(prev => ({ ...prev, [id]: m }))
        setEditorName(detail.name)
        setEditorContent(content)
        setOriginalName(detail.name)
        setOriginalContent(content)
        setIsPublic(detail.isPublic ?? false)
        setPublicStatus(null)
        setPreviewContent(m.vowClass === 'C' ? injectVowFrame(content, 0) : content)
        setLoadingDetail(false)
      })
      .catch(() => setLoadingDetail(false))
  }, [isDirty])

  // Auto-select the first template when the list initially loads.
  // Must live AFTER selectTemplate is declared (avoids temporal dead zone).
  // A stable ref stores the latest selectTemplate so the effect deps stay
  // minimal and re-running on isDirty changes doesn't re-trigger selection.
  const selectTemplateRef = useRef(selectTemplate)
  useEffect(() => { selectTemplateRef.current = selectTemplate }, [selectTemplate])
  const autoSelectDoneRef = useRef(false)
  useEffect(() => {
    if (autoSelectDoneRef.current || loadingList || selectedId !== null || templates.length === 0) return
    autoSelectDoneRef.current = true
    selectTemplateRef.current(templates[0].id)
  }, [loadingList, templates, selectedId])

  // ── New template ──────────────────────────────────────────────────────────
  const handleNew = useCallback(async () => {
    if (isDirty) {
      if (!await confirm('You have unsaved changes. Discard them?', { title: 'UNSAVED CHANGES' })) return
    }
    // Pick a name that doesn't clash with any existing template (client-side check).
    let newName = 'New Template'
    let counter = 2
    while (templates.some(t => t.name === newName)) {
      newName = `New Template (${counter++})`
    }

    setSaveStatus('saving')
    try {
      const res = await authFetch('/api/templates', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name: newName, htmlContent: '' }),
      })
      if (!res?.ok) throw new Error(`HTTP ${res?.status}`)
      const tpl = await res.json()
      isNewRef.current = true
      changedRef.current = true
      setTemplates(prev => [tpl, ...prev])
      setSelectedId(tpl.id)
      setEditorName(tpl.name)
      setEditorContent('')
      setOriginalName(tpl.name)
      setOriginalContent('')
      setPreviewContent('')
      setFrameIdx(0)
      setLoadingDetail(false)
      setSaveStatus(null)
      // Seed metaMap so the list badge and filters work for the new (empty) template.
      setMetaMap(prev => ({ ...prev, [tpl.id]: parseVowMeta('') }))
      setTimeout(() => document.getElementById('tmm-name-input')?.select(), 50)
    } catch {
      setSaveStatus('error')
      setTimeout(() => setSaveStatus(null), 3000)
    }
  }, [isDirty, templates])

  // ── Save ──────────────────────────────────────────────────────────────────
  const handleSave = useCallback(async () => {
    if (!selectedId) return
    setSaving(true)
    setSaveStatus('saving')
    try {
      const res = await authFetch(`/api/templates/${selectedId}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name: editorName, htmlContent: editorContent }),
      })
      if (!res?.ok) throw new Error(`HTTP ${res?.status}`)
      setOriginalName(editorName)
      setOriginalContent(editorContent)
      setTemplates(prev => prev.map(t => t.id === selectedId ? { ...t, name: editorName } : t))
      // Keep metaMap in sync so the list badge and class/category filters reflect
      // the latest class, category, and frame settings without needing a page reload.
      setMetaMap(prev => ({ ...prev, [selectedId]: parseVowMeta(editorContent) }))
      changedRef.current = true
      setSaveStatus('saved')
      setTimeout(() => setSaveStatus(null), 2000)
    } catch {
      setSaveStatus('error')
      setTimeout(() => setSaveStatus(null), 3000)
    } finally {
      setSaving(false)
    }
  }, [selectedId, editorName, editorContent])

  // ── Toggle IsPublic ───────────────────────────────────────────────────────
  const handleTogglePublic = useCallback(async () => {
    if (!selectedId) return
    const next = !isPublic
    setIsPublic(next)
    setPublicStatus('saving')
    try {
      const res = await authFetch(`/api/templates/${selectedId}/public`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ isPublic: next }),
      })
      if (!res?.ok) throw new Error(`HTTP ${res?.status}`)
      setPublicStatus('ok')
      setTimeout(() => setPublicStatus(null), 1500)
    } catch {
      setIsPublic(!next) // revert on error
      setPublicStatus('err')
      setTimeout(() => setPublicStatus(null), 2500)
    }
  }, [selectedId, isPublic])

  // ── Save & Grab ───────────────────────────────────────────────────────────
  const handleSaveAndGrab = useCallback(async () => {
    await handleSave()
    if (!selectedId) return
    setSaveStatus('saving')
    try {
      const res = await authFetch(`/api/templates/${selectedId}/grab`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({}),
      })
      if (!res?.ok) throw new Error(`HTTP ${res?.status}`)
      setSaveStatus('saved')
      setTimeout(() => setSaveStatus(null), 2000)
    } catch {
      setSaveStatus('error')
      setTimeout(() => setSaveStatus(null), 3000)
    }
  }, [handleSave, selectedId])

  // ── Pin to canvas ─────────────────────────────────────────────────────────
  // Writes a default position to localStorage (and syncs to the server) so the
  // template node appears on the FlowCanvas pipeline view without a full refresh.
  const NODE_POSITIONS_KEY = 'viewowl:node-positions'
  const handlePinToCanvas = useCallback(async () => {
    if (!selectedId) return
    const posKey = `template-${selectedId}`
    let positions = {}
    try { positions = JSON.parse(localStorage.getItem(NODE_POSITIONS_KEY) || '{}') } catch { /* ignore */ }
    if (positions[posKey]) {
      setPinStatus('already')
      setTimeout(() => setPinStatus(null), 2000)
      return
    }
    // Stagger position using only the count of template nodes already on canvas.
    // Using the total key count (including device-* entries) pushes new nodes
    // 1600+ px off-screen when many positions are stored.
    const templateOffset = Object.keys(positions).filter(k => k.startsWith('template-')).length
    positions[posKey] = { x: 120 + (templateOffset % 4) * 230, y: 100 + Math.floor(templateOffset / 4) * 320 }
    try { localStorage.setItem(NODE_POSITIONS_KEY, JSON.stringify(positions)) } catch { /* storage full */ }
    // Persist to server so the position survives localStorage clears.
    await authFetch('/api/flow-layout', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ positions }),
    }).catch(() => {})
    // Mark as changed so closing this modal triggers a dashboard reload —
    // that re-run of FlowCanvas's useEffect will pick up the saved position
    // and display the node on the canvas without requiring a second interaction.
    changedRef.current = true
    setPinStatus('added')
    setTimeout(() => setPinStatus(null), 2500)
  }, [selectedId])

  const pinBtnLabel = pinStatus === 'added' ? '✓ ADDED' : pinStatus === 'already' ? '✓ ON CANVAS' : '⊞ TO CANVAS'

  // ── Delete ────────────────────────────────────────────────────────────────
  const handleDelete = useCallback(async () => {
    if (!selectedId) return
    const name = templates.find(t => t.id === selectedId)?.name ?? 'this template'
    if (!await confirm(`Delete "${name}"?\n\nControllers using it will be switched to the default template.`, { title: 'DELETE TEMPLATE', danger: true })) return
    const res = await authFetch(`/api/templates/${selectedId}`, { method: 'DELETE' })
    if (res?.ok || res?.status === 204) {
      changedRef.current = true
      setTemplates(prev => prev.filter(t => t.id !== selectedId))
      setSelectedId(null)
      setEditorName('')
      setEditorContent('')
      setOriginalName('')
      setOriginalContent('')
    }
  }, [selectedId, templates])

  // ── Run preview ───────────────────────────────────────────────────────────
  const handleRun = useCallback(() => {
    if (isClassC) {
      setPreviewContent(injectVowFrame(editorContent, frameIdx))
    } else {
      setPreviewContent(editorContent)
    }
  }, [isClassC, editorContent, frameIdx])

  // ── Metadata attribute helpers ────────────────────────────────────────────
  const setMeta = useCallback((attr, value) => {
    setEditorContent(prev => setVowAttr(prev, attr, value))
  }, [])

  // ── Keyboard shortcuts ────────────────────────────────────────────────────
  useEffect(() => {
    const onKey = (e) => {
      if (e.key === 'Escape') onClose(changedRef.current)
      if ((e.ctrlKey || e.metaKey) && e.key === 's') { e.preventDefault(); handleSave() }
      if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') { e.preventDefault(); handleRun() }
      if (isClassC && e.key === 'ArrowLeft'  && e.target === document.body) setFrameIdx(i => Math.max(0, i - 1))
      if (isClassC && e.key === 'ArrowRight' && e.target === document.body) setFrameIdx(i => Math.min(meta.frameCount - 1, i + 1))
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  })

  // ── Category actions ──────────────────────────────────────────────────────
  const handleCatRename = useCallback(async (id) => {
    if (!editingCatLabel.trim()) return
    const res = await authFetch(`/api/template-categories/${id}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ label: editingCatLabel.trim() }),
    })
    if (res?.ok) {
      const updated = await res.json()
      setCategories(prev => prev.map(c => c.id === id ? updated : c))
      setEditingCatId(null)
    }
  }, [editingCatLabel])

  const handleCatDelete = useCallback(async (id) => {
    const cat = categories.find(c => c.id === id)
    if (!cat || cat.isSystem) return
    if (!await confirm(`Delete category "${cat.label}"?\nAll its templates will move to OTHER.`, { title: 'DELETE CATEGORY', danger: true })) return
    const res = await authFetch(`/api/template-categories/${id}`, { method: 'DELETE' })
    if (res?.ok || res?.status === 204) {
      setCategories(prev => prev.filter(c => c.id !== id))
    }
  }, [categories])

  const handleCatAdd = useCallback(async () => {
    const label = newCatLabel.trim()
    if (!label) return
    setCatAddError(null)
    setCatAddBusy(true)
    try {
      const res = await authFetch('/api/template-categories', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ label }),
      })
      if (!res) return // redirected to login
      if (res.ok) {
        const created = await res.json()
        setCategories(prev => [...prev, created])
        setNewCatLabel('')
      } else {
        // Try to extract a server-side error message, fall back to HTTP status
        let msg = `HTTP ${res.status}`
        try {
          const body = await res.json()
          if (body?.error) msg = body.error
          else if (body?.title) msg = body.title
        } catch { /* non-JSON body — keep status msg */ }
        setCatAddError(msg)
      }
    } catch (err) {
      setCatAddError(err?.message ?? 'Network error')
    } finally {
      setCatAddBusy(false)
    }
  }, [newCatLabel])

  // ── Filtered template list (must be declared before handleListKeyDown) ──────
  const filteredTemplates = templates.filter(t => {
    const m      = metaMap[t.id]
    const tClass = m?.vowClass ?? 'A'
    const tCat   = m?.category ?? 'other'
    const matchClass  = classFilter === 'all' || tClass === classFilter
    const matchCat    = catFilter   === 'all' || tCat   === catFilter
    const matchSearch = !searchQuery || t.name.toLowerCase().includes(searchQuery.toLowerCase())
    return matchClass && matchCat && matchSearch
  })

  // ── List keyboard navigation ──────────────────────────────────────────────
  const handleListKeyDown = useCallback((e) => {
    if (e.key !== 'ArrowUp' && e.key !== 'ArrowDown') return
    e.preventDefault()
    const idx = filteredTemplates.findIndex(t => t.id === selectedId)
    let next
    if (e.key === 'ArrowDown') next = filteredTemplates[idx < filteredTemplates.length - 1 ? idx + 1 : 0]
    else                        next = filteredTemplates[idx > 0 ? idx - 1 : filteredTemplates.length - 1]
    if (next) selectTemplate(next.id)
  }, [filteredTemplates, selectedId, selectTemplate])

  // ── Export ────────────────────────────────────────────────────────────────
  const [exportBusy, setExportBusy] = useState(false)
  const importInputRef = useRef(null)

  const handleExport = useCallback(async () => {
    setExportBusy(true)
    try {
      const res = await authFetch('/api/templates/export')
      if (!res?.ok) throw new Error(`HTTP ${res?.status}`)
      const blob = await res.blob()
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      const cd = res.headers.get('content-disposition') ?? ''
      const match = /filename="?([^";\n]+)"?/i.exec(cd)
      a.download = match?.[1] ?? 'viewowl-templates.zip'
      a.click()
      URL.revokeObjectURL(url)
    } catch {
      // Error is visible from the browser's download failure — no extra UI needed.
    } finally {
      setExportBusy(false)
    }
  }, [])

  // ── Import — two-phase: dry-run preview → confirm → commit ────────────────
  //
  // importState shape:
  //   null                                      — idle
  //   { phase: 'checking', fileName }           — dry-run call in progress
  //   { phase: 'preview',  fileName, file, preview: {wouldCreate, wouldUpdate, errors} }
  //   { phase: 'importing' }                    — commit call in progress
  //   { phase: 'done', result: {created, updated, errors} }  — auto-dismisses
  const [importState, setImportState] = useState(null)

  // Drag-and-drop state — counter tracks enter/leave across child elements.
  const [dragOver, setDragOver]   = useState(false)
  const dragCounterRef            = useRef(0)

  // Phase 1: validate the ZIP with a dry-run, then show preview.
  const handleImportFile = useCallback(async (file) => {
    if (!file) return
    if (!file.name.toLowerCase().endsWith('.zip')) return

    setImportState({ phase: 'checking', fileName: file.name })
    try {
      const form = new FormData()
      form.append('file', file)
      const res = await authFetch('/api/templates/import?dryRun=true', { method: 'POST', body: form })
      if (!res?.ok) throw new Error(`HTTP ${res?.status}`)
      const preview = await res.json()
      setImportState({ phase: 'preview', fileName: file.name, file, preview })
    } catch (err) {
      setImportState({ phase: 'done', result: { created: 0, updated: 0, errors: [err.message ?? 'Import failed'] } })
      setTimeout(() => setImportState(null), 6000)
    }
  }, [])

  // Phase 2: user confirmed — commit the actual import.
  const handleImportConfirm = useCallback(async () => {
    if (importState?.phase !== 'preview') return
    const { file } = importState

    setImportState({ phase: 'importing' })
    try {
      const form = new FormData()
      form.append('file', file)
      const res = await authFetch('/api/templates/import', { method: 'POST', body: form })
      if (!res?.ok) throw new Error(`HTTP ${res?.status}`)
      const result = await res.json()
      changedRef.current = true
      loadTemplates()
      setImportState({ phase: 'done', result })
      setTimeout(() => setImportState(null), 5000)
    } catch (err) {
      setImportState({ phase: 'done', result: { created: 0, updated: 0, errors: [err.message ?? 'Import failed'] } })
      setTimeout(() => setImportState(null), 6000)
    }
  }, [importState, loadTemplates])

  // Drag-and-drop handlers — attached to the modal shell so the entire surface accepts drops.
  const handleDragEnter = useCallback((e) => {
    e.preventDefault()
    dragCounterRef.current++
    if (dragCounterRef.current === 1) setDragOver(true)
  }, [])

  const handleDragLeave = useCallback(() => {
    dragCounterRef.current--
    if (dragCounterRef.current === 0) setDragOver(false)
  }, [])

  const handleDragOver = useCallback((e) => { e.preventDefault() }, [])

  const handleDrop = useCallback((e) => {
    e.preventDefault()
    dragCounterRef.current = 0
    setDragOver(false)
    const file = e.dataTransfer.files?.[0]
    if (file) handleImportFile(file)
  }, [handleImportFile])

  // ── Save button label ─────────────────────────────────────────────────────
  const saveBtnLabel = saveStatus === 'saving' ? 'SAVING…'
    : saveStatus === 'saved'  ? '✓ SAVED'
    : saveStatus === 'error'  ? '✗ ERROR'
    : '💾 SAVE'

  // ── Render ────────────────────────────────────────────────────────────────
  // Total templates that would be touched (used in confirm button label)
  const importTotal = importState?.phase === 'preview'
    ? (importState.preview.wouldCreate.length + importState.preview.wouldUpdate.length)
    : 0

  return (
    <div className="tmm-overlay" onClick={e => { if (e.target === e.currentTarget) onClose(changedRef.current) }}>
      <div
        className={`tmm-modal hud-panel${dragOver ? ' tmm-modal-drag-over' : ''}`}
        onDragEnter={handleDragEnter}
        onDragLeave={handleDragLeave}
        onDragOver={handleDragOver}
        onDrop={handleDrop}
      >

        {/* ── Top bar ──────────────────────────────────────────────────── */}
        <div className="tmm-topbar">
          <div className="tmm-tabs">
            <button className={`tmm-tab${tab === 'templates'  ? ' active' : ''}`} onClick={() => setTab('templates')}>◈ TEMPLATES</button>
            <button className={`tmm-tab${tab === 'categories' ? ' active' : ''}`} onClick={() => setTab('categories')}>⊞ CATEGORIES</button>
          </div>
          <button className="tmm-close" onClick={() => onClose(changedRef.current)} title="Close (Esc)">✕</button>
        </div>

        {/* ── Drag-and-drop overlay ────────────────────────────────────
             Shown while the user holds a file over the modal.          */}
        {dragOver && (
          <div className="tmm-drag-overlay">
            <div className="tmm-drag-hint">↑ DROP ZIP TO IMPORT</div>
          </div>
        )}

        {/* ── Import preview overlay ────────────────────────────────────
             Direct child of tmm-modal so it escapes tmm-body's
             overflow:hidden clip. Covers everything below the topbar.  */}
        {importState && importState.phase !== 'done' && (
          <div className="tmm-import-overlay">
            <div className="tmm-import-card">

              {importState.phase === 'checking' && (
                <>
                  <div className="tmm-import-card-title">ANALYSING ARCHIVE…</div>
                  <div className="tmm-import-card-file">📦 {importState.fileName}</div>
                </>
              )}

              {importState.phase === 'importing' && (
                <>
                  <div className="tmm-import-card-title">IMPORTING…</div>
                  <div className="tmm-import-card-file">Writing templates to database</div>
                </>
              )}

              {importState.phase === 'preview' && (
                <>
                  <div className="tmm-import-card-title">IMPORT PREVIEW</div>
                  <div className="tmm-import-card-file">📦 {importState.fileName}</div>

                  <div className="tmm-import-sections">
                    <div className="tmm-import-section">
                      <div className="tmm-import-section-head tmm-import-head-create">
                        ✦ {importState.preview.wouldCreate.length} will be CREATED
                      </div>
                      {importState.preview.wouldCreate.length > 0 && (
                        <ul className="tmm-import-names">
                          {importState.preview.wouldCreate.map(item => (
                              <li key={item.name} className="tmm-import-item">
                                <span className="tmm-import-item-name">{item.name}</span>
                                <span className={`tmm-import-item-class tmm-cls-${(item.class ?? 'a').toLowerCase()}`}>{item.class}</span>
                                <span className="tmm-import-item-cat">{item.category}</span>
                              </li>
                            ))}
                        </ul>
                      )}
                    </div>

                    <div className="tmm-import-section">
                      <div className="tmm-import-section-head tmm-import-head-update">
                        ⟳ {importState.preview.wouldUpdate.length} will be UPDATED
                      </div>
                      {importState.preview.wouldUpdate.length > 0 && (
                        <ul className="tmm-import-names">
                          {importState.preview.wouldUpdate.map(item => (
                              <li key={item.name} className="tmm-import-item">
                                <span className="tmm-import-item-name">{item.name}</span>
                                <span className={`tmm-import-item-class tmm-cls-${(item.class ?? 'a').toLowerCase()}`}>{item.class}</span>
                                <span className="tmm-import-item-cat">{item.category}</span>
                              </li>
                            ))}
                        </ul>
                      )}
                    </div>

                    {importState.preview.errors.length > 0 && (
                      <div className="tmm-import-section">
                        <div className="tmm-import-section-head tmm-import-head-error">
                          ✗ {importState.preview.errors.length} errors
                        </div>
                        <ul className="tmm-import-names tmm-import-err-list">
                          {importState.preview.errors.map((msg, i) => <li key={i}>{msg}</li>)}
                        </ul>
                      </div>
                    )}
                  </div>

                  <div className="tmm-import-card-actions">
                    <button
                      className="tmm-import-action-cancel"
                      onClick={() => setImportState(null)}
                    >
                      CANCEL
                    </button>
                    <button
                      className="tmm-import-action-confirm"
                      onClick={handleImportConfirm}
                      disabled={importTotal === 0}
                    >
                      ↑ IMPORT {importTotal > 0
                        ? `${importTotal} TEMPLATE${importTotal !== 1 ? 'S' : ''}`
                        : '(NOTHING TO DO)'}
                    </button>
                  </div>
                </>
              )}

            </div>
          </div>
        )}

        {/* ══════════════════════════════════════════════════════════════ */}
        {/* TAB: TEMPLATES                                                */}
        {/* ══════════════════════════════════════════════════════════════ */}
        {tab === 'templates' && (
          <div className="tmm-body">

            {/* ── New / Export / Import row ──────────────────────────── */}
            <div className="tmm-new-row">
              <button className="tmm-new-btn" onClick={handleNew}>+ NEW</button>

              <button
                className={`tmm-io-btn tmm-io-export${exportBusy ? ' tmm-io-busy' : ''}`}
                onClick={handleExport}
                disabled={exportBusy}
                title="Export all templates as ZIP"
              >
                {exportBusy ? '…' : '↓ EXPORT'}
              </button>

              <button
                className="tmm-io-btn tmm-io-import"
                onClick={() => importInputRef.current?.click()}
                disabled={importState?.phase === 'checking' || importState?.phase === 'importing'}
                title="Import templates from ZIP (or drag & drop)"
              >
                {importState?.phase === 'checking' ? '…' : '↑ IMPORT'}
              </button>

              {/* Hidden file picker — triggered by the IMPORT button or drag-and-drop */}
              <input
                ref={importInputRef}
                type="file"
                accept=".zip"
                style={{ display: 'none' }}
                onChange={e => { const f = e.target.files?.[0]; e.target.value = ''; handleImportFile(f) }}
              />

              {/* Done toast — auto-dismisses after 5 s */}
              {importState?.phase === 'done' && (
                <span className={`tmm-import-result${importState.result.errors?.length ? ' tmm-import-err' : ''}`}>
                  {importState.result.errors?.length
                    ? `✗ ${importState.result.errors[0]}`
                    : `✓ ${importState.result.created} created, ${importState.result.updated} updated`}
                </span>
              )}

              {isDirty && selectedId && (
                <span className="tmm-dirty-notice">● Unsaved changes</span>
              )}
            </div>

            {/* ── Three-column layout ────────────────────────────────── */}
            <div className="tmm-columns">

              {/* ── Column 1: Filters ──────────────────────────────── */}
              <div className="tmm-col-filters">
                <div className="tmm-filter-section">
                  <div className="tmm-filter-heading">CLASS</div>
                  {CLASS_FILTERS.map(f => (
                    <label key={f.value} className="tmm-radio-row">
                      <input
                        type="radio"
                        name="classFilter"
                        value={f.value}
                        checked={classFilter === f.value}
                        onChange={() => setClassFilter(f.value)}
                      />
                      <span>{f.label}</span>
                    </label>
                  ))}
                </div>

                <div className="tmm-filter-section">
                  <div className="tmm-filter-heading">CATEGORY</div>
                  <label className="tmm-radio-row">
                    <input type="radio" name="catFilter" value="all" checked={catFilter === 'all'} onChange={() => setCatFilter('all')} />
                    <span>ALL</span>
                  </label>
                  {categories.map(c => (
                    <label key={c.slug} className="tmm-radio-row">
                      <input type="radio" name="catFilter" value={c.slug} checked={catFilter === c.slug} onChange={() => setCatFilter(c.slug)} />
                      <span>{c.label}</span>
                      {c.templateCount > 0 && <span className="tmm-cat-count">{c.templateCount}</span>}
                    </label>
                  ))}
                </div>
              </div>

              {/* ── Column 2: Template list ─────────────────────────── */}
              <div className="tmm-col-list">
                <input
                  className="tmm-search"
                  placeholder="Search…"
                  value={searchQuery}
                  onChange={e => setSearchQuery(e.target.value)}
                  spellCheck={false}
                />
                {loadingList ? (
                  <div className="tmm-list-loading">LOADING…</div>
                ) : filteredTemplates.length === 0 ? (
                  <div className="tmm-list-empty">No templates</div>
                ) : (
                  <div
                    ref={listRef}
                    className="tmm-list"
                    tabIndex={0}
                    onKeyDown={handleListKeyDown}
                  >
                    {filteredTemplates.map(t => {
                      const m = metaMap[t.id]
                      return (
                        <button
                          key={t.id}
                          className={`tmm-list-item${selectedId === t.id ? ' active' : ''}`}
                          onClick={() => selectTemplate(t.id)}
                        >
                          <span className="tmm-item-name">
                            {t.name}
                            {selectedId === t.id && isDirty && (
                              <span className="tmm-item-dirty" title="Unsaved changes">●</span>
                            )}
                          </span>
                          {m && (
                            <span className={`tmm-item-class tmm-class-${m.vowClass?.toLowerCase()}`}>
                              {m.vowClass}
                            </span>
                          )}
                        </button>
                      )
                    })}
                  </div>
                )}
              </div>

              {/* ── Column 3: Editor ────────────────────────────────── */}
              <div className="tmm-col-editor">
                {!selectedId ? (
                  <div className="tmm-editor-empty">
                    <span>Select a template or create a new one</span>
                  </div>
                ) : loadingDetail ? (
                  <div className="tmm-editor-empty"><span>LOADING…</span></div>
                ) : (
                  <>
                    {/* Name */}
                    <div className="tmm-field-row">
                      <label className="tmm-field-label">NAME</label>
                      <input
                        id="tmm-name-input"
                        className="tmm-name-input"
                        value={editorName}
                        onChange={e => setEditorName(e.target.value)}
                        spellCheck={false}
                      />
                    </div>

                    {/* ── Metadata editor ──────────────────────────── */}
                    <div className="tmm-meta-bar">
                      {/* Class */}
                      <div className="tmm-meta-group">
                        <span className="tmm-meta-key">CLASS</span>
                        {['A', 'B', 'C'].map(cls => (
                          <button
                            key={cls}
                            className={`tmm-meta-cls-btn tmm-cls-${cls.toLowerCase()}${meta.vowClass === cls ? ' active' : ''}`}
                            onClick={() => setMeta('class', cls)}
                            title={`Set class ${cls}`}
                          >{cls}</button>
                        ))}
                      </div>

                      <div className="tmm-meta-sep" />

                      {/* Category */}
                      <div className="tmm-meta-group">
                        <span className="tmm-meta-key">CAT</span>
                        <select
                          className="tmm-meta-select"
                          value={meta.category}
                          onChange={e => setMeta('category', e.target.value)}
                        >
                          {categories.length > 0
                            ? categories.map(c => <option key={c.slug} value={c.slug}>{c.label}</option>)
                            : <option value="other">OTHER</option>
                          }
                        </select>
                      </div>

                      <div className="tmm-meta-sep" />

                      {/* Frames (Class C only) */}
                      {isClassC && (
                        <div className="tmm-meta-group">
                          <span className="tmm-meta-key">FRAMES</span>
                          <input
                            type="number" min="1" max="16"
                            className="tmm-meta-num"
                            value={meta.frameCount}
                            onChange={e => setMeta('frames', e.target.value)}
                          />
                        </div>
                      )}

                      {/* FPS (Class C only) */}
                      {isClassC && (
                        <div className="tmm-meta-group">
                          <span className="tmm-meta-key">FPS</span>
                          <input
                            type="number" min="1" max="30"
                            className="tmm-meta-num"
                            value={meta.fps}
                            onChange={e => setMeta('fps', e.target.value)}
                          />
                        </div>
                      )}

                      {isClassC && <div className="tmm-meta-sep" />}

                      {/* Refresh */}
                      <div className="tmm-meta-group">
                        <span className="tmm-meta-key">REFRESH</span>
                        <input
                          type="number" min="0"
                          className="tmm-meta-num"
                          value={meta.refresh}
                          onChange={e => setMeta('refresh', e.target.value)}
                        />
                        <span className="tmm-meta-unit">min</span>
                      </div>

                      <div className="tmm-meta-sep" />

                      {/* Landing page visibility */}
                      <div className="tmm-meta-group">
                        <span className="tmm-meta-key">PUBLIC</span>
                        <button
                          className={`tmm-public-btn${isPublic ? ' active' : ''}${publicStatus === 'err' ? ' err' : ''}`}
                          onClick={handleTogglePublic}
                          disabled={publicStatus === 'saving'}
                          title={isPublic ? 'Shown on landing page — click to hide' : 'Hidden from landing page — click to show'}
                        >
                          {publicStatus === 'saving' ? '…' : isPublic ? '●' : '○'}
                        </button>
                      </div>
                    </div>

                    {/* CodeMirror editor */}
                    <div
                      className="tmm-editor-wrap"
                      onWheel={e => e.stopPropagation()}
                    >
                      <CodeMirror
                        value={editorContent}
                        height="100%"
                        theme={vscodeDark}
                        extensions={[html()]}
                        onChange={val => setEditorContent(val)}
                        basicSetup={{
                          lineNumbers: true,
                          foldGutter: true,
                          bracketMatching: true,
                          autocompletion: true,
                          highlightActiveLine: true,
                        }}
                      />
                    </div>

                    {/* Preview */}
                    <div className="tmm-preview-label">
                      {isClassC
                        ? `CLASS C · ${meta.frameCount} FRAMES · ${meta.fps} FPS`
                        : 'PREVIEW — 480 × 320'}
                    </div>
                    <div className="tmm-preview-wrap">
                      <iframe
                        className="tmm-preview-iframe"
                        srcDoc={previewContent}
                        width="480"
                        height="320"
                        sandbox="allow-scripts"
                        title="Template preview"
                      />
                    </div>

                    {/* Frame navigator (Class C only) */}
                    {isClassC && (
                      <div className="tmm-frame-nav">
                        <button
                          className="tmm-fnav-btn"
                          onClick={() => setFrameIdx(i => Math.max(0, i - 1))}
                          disabled={frameIdx === 0 || isPlaying}
                        >◀</button>

                        {/* Auto-play toggle */}
                        <button
                          className={`tmm-fnav-btn tmm-fnav-play${isPlaying ? ' playing' : ''}`}
                          onClick={() => setIsPlaying(v => !v)}
                          title={isPlaying ? 'Pause' : `Play at ${meta.fps} FPS`}
                        >{isPlaying ? '⏸' : '▶'}</button>

                        <span className="tmm-fnav-info">
                          {isPlaying
                            ? `▶ ${meta.fps} FPS`
                            : `${frameIdx + 1} / ${meta.frameCount}`}
                        </span>

                        <button
                          className="tmm-fnav-btn"
                          onClick={() => setFrameIdx(i => Math.min(meta.frameCount - 1, i + 1))}
                          disabled={frameIdx === meta.frameCount - 1 || isPlaying}
                        >▶</button>
                      </div>
                    )}

                    {/* Actions */}
                    <div className="tmm-actions">
                      <button className="tmm-btn tmm-btn-run" onClick={handleRun} title="Ctrl+Enter">
                        ▶ RUN
                      </button>
                      <button
                        className={`tmm-btn tmm-btn-save${saveStatus === 'saved' ? ' status-ok' : saveStatus === 'error' ? ' status-err' : ''}`}
                        onClick={handleSave}
                        disabled={saving}
                        title="Ctrl+S"
                      >
                        {saveBtnLabel}
                      </button>
                      <button className="tmm-btn tmm-btn-grab" onClick={handleSaveAndGrab}>
                        📷 SAVE &amp; GRAB
                      </button>
                      <button
                        className={`tmm-btn tmm-btn-pin${pinStatus === 'added' ? ' status-ok' : ''}`}
                        onClick={handlePinToCanvas}
                        title="Add this template to the pipeline canvas"
                      >
                        {pinBtnLabel}
                      </button>
                      <span className="tmm-actions-spacer" />
                      <button className="tmm-btn tmm-btn-delete" onClick={handleDelete}>
                        🗑 DELETE
                      </button>
                    </div>

                    <div className="tmm-shortcuts">
                      Ctrl+S — Save · Ctrl+Enter — Run
                      {isClassC && ' · ← → — Frames'}
                      · Esc — Close
                    </div>
                  </>
                )}
              </div>

            </div>{/* /tmm-columns */}
          </div>
        )}

        {/* ══════════════════════════════════════════════════════════════ */}
        {/* TAB: CATEGORIES                                               */}
        {/* ══════════════════════════════════════════════════════════════ */}
        {tab === 'categories' && (
          <div className="tmm-cat-body">

            {/* Add new category */}
            <div className="tmm-cat-add-row">
              <input
                className="tmm-cat-add-input"
                placeholder="New category name…"
                value={newCatLabel}
                onChange={e => { setNewCatLabel(e.target.value); setCatAddError(null) }}
                onKeyDown={e => { if (e.key === 'Enter') handleCatAdd() }}
                spellCheck={false}
              />
              <button
                className="tmm-cat-add-btn"
                onClick={handleCatAdd}
                disabled={!newCatLabel.trim() || catAddBusy}
              >{catAddBusy ? '…' : '+ ADD'}</button>
            </div>
            {catAddError && (
              <div className="tmm-cat-add-error">⚠ {catAddError}</div>
            )}

            {catLoading ? (
              <div className="tmm-list-loading">LOADING…</div>
            ) : categories.length === 0 ? (
              <div className="tmm-list-empty">No categories yet</div>
            ) : (
              <div className="tmm-cat-table">
                {categories.map(c => (
                  <div key={c.id} className="tmm-cat-row">
                    {editingCatId === c.id ? (
                      <input
                        className="tmm-cat-edit-input"
                        value={editingCatLabel}
                        onChange={e => setEditingCatLabel(e.target.value)}
                        onKeyDown={e => {
                          if (e.key === 'Enter') handleCatRename(c.id)
                          if (e.key === 'Escape') setEditingCatId(null)
                        }}
                        autoFocus
                        spellCheck={false}
                      />
                    ) : (
                      <span className="tmm-cat-label">{c.label}</span>
                    )}
                    <span className="tmm-cat-slug">{c.slug}</span>
                    <span className="tmm-cat-count-cell">{c.templateCount} templates</span>
                    <div className="tmm-cat-actions">
                      {editingCatId === c.id ? (
                        <>
                          <button className="tmm-cat-btn tmm-cat-confirm" onClick={() => handleCatRename(c.id)}>✓</button>
                          <button className="tmm-cat-btn" onClick={() => setEditingCatId(null)}>✕</button>
                        </>
                      ) : (
                        <button
                          className="tmm-cat-btn"
                          onClick={() => { setEditingCatId(c.id); setEditingCatLabel(c.label) }}
                          title="Rename"
                        >✎</button>
                      )}
                      {c.isSystem ? (
                        <span className="tmm-cat-lock" title="System category — cannot be deleted">🔒</span>
                      ) : (
                        <button
                          className="tmm-cat-btn tmm-cat-delete"
                          onClick={() => handleCatDelete(c.id)}
                          title="Delete — templates move to OTHER"
                        >✕</button>
                      )}
                    </div>
                  </div>
                ))}
              </div>
            )}
          </div>
        )}

      </div>
    </div>
  )
}

export default TemplateManagerModal
