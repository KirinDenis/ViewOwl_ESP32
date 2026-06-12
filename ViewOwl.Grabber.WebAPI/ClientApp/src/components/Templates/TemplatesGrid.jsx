import { useState, useEffect, useCallback } from 'react'
import TemplateCard from './TemplateCard'
import TemplateEditorModal from './TemplateEditorModal'
import { authFetch } from '../../utils/auth'
import { confirm } from '../../utils/confirm'
import { VOW_CATEGORIES } from '../../utils/vowMeta'
import './TemplatesGrid.css'

function TemplatesGrid({ grabEvent, onEvent }) {
  const [templates, setTemplates]         = useState([])
  const [loading, setLoading]             = useState(true)
  const [error, setError]                 = useState(null)
  const [editingTemplate, setEditingTemplate] = useState(null)
  const [activeCategory, setActiveCategory]   = useState('all')
  // Map of templateId -> VowMeta, populated as each card loads its HTML
  const [metaMap, setMetaMap]             = useState({})

  const loadTemplates = useCallback(() => {
    setLoading(true)
    authFetch('/api/templates')
      .then(r => { if (!r) return; if (!r.ok) throw new Error(`HTTP ${r.status}`); return r.json() })
      .then(data => { if (data) { setTemplates(data); setLoading(false) } })
      .catch(err => { setError(err.message); setLoading(false) })
  }, [])

  useEffect(() => { loadTemplates() }, [loadTemplates])

  // Called by each TemplateCard once it has parsed its HTML metadata
  const handleMetaLoaded = useCallback((templateId, meta) => {
    setMetaMap(prev => ({ ...prev, [templateId]: meta }))
  }, [])

  const handleEdit = (template) => {
    authFetch(`/api/templates/${template.id}`)
      .then(r => r?.json())
      .then(detail => {
        if (detail) {
          setEditingTemplate({
            id: detail.id,
            name: detail.name,
            htmlContent: detail.htmlContent
          })
        }
      })
      .catch(() => {})
  }

  const handleSave = async (id, name, htmlContent) => {
    const res = await authFetch(`/api/templates/${id}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name, htmlContent })
    })
    if (res?.ok) {
      onEvent?.('Template saved', 'success')
      setTemplates(prev => prev.map(t => t.id === id ? { ...t, name } : t))
      loadTemplates()
      setEditingTemplate(prev => prev ? { ...prev, name, htmlContent } : null)
    }
  }

  const handleGrab = async (id) => {
    const res = await authFetch(`/api/templates/${id}/grab`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({})
    })
    if (res?.ok) {
      onEvent?.('Grab queued — preview will update shortly', 'info')
    } else {
      const msg = await res?.text().catch(() => '')
      onEvent?.(`Grab failed${msg ? ': ' + msg : ''}`, 'error')
    }
  }

  const handleDelete = async (id, name) => {
    if (!await confirm(`Delete template "${name}"?`, { title: 'DELETE TEMPLATE', danger: true })) return
    const res = await authFetch(`/api/templates/${id}`, { method: 'DELETE' })
    if (res?.ok) {
      setTemplates(prev => prev.filter(t => t.id !== id))
      if (editingTemplate?.id === id) setEditingTemplate(null)
      onEvent?.('Template deleted', 'info')
    } else {
      onEvent?.('Failed to delete template', 'error')
    }
  }

  // Filter templates by active category using the collected metadata
  const filteredTemplates = activeCategory === 'all'
    ? templates
    : templates.filter(t => (metaMap[t.id]?.category ?? 'other') === activeCategory)

  // Count per category for the filter bar (only categories with templates)
  const categoryCounts = VOW_CATEGORIES.reduce((acc, cat) => {
    if (cat.slug === 'all') {
      acc[cat.slug] = templates.length
    } else {
      acc[cat.slug] = templates.filter(
        t => (metaMap[t.id]?.category ?? 'other') === cat.slug
      ).length
    }
    return acc
  }, {})

  if (loading) {
    return (
      <div className="templates-loading">
        <span className="glow-text">LOADING TEMPLATES...</span>
      </div>
    )
  }

  if (error) {
    return (
      <div className="templates-loading">
        <span className="templates-error">ERROR: {error}</span>
        <button className="reload-btn" onClick={loadTemplates}>RETRY</button>
      </div>
    )
  }

  return (
    <div className="templates-grid-view">
      <div className="templates-toolbar">
        <span className="templates-title glow-text">◈ TEMPLATES</span>
        <span className="templates-count">
          {filteredTemplates.length}{activeCategory !== 'all' ? `/${templates.length}` : ''}&nbsp;
          TEMPLATE{templates.length !== 1 ? 'S' : ''}
        </span>
      </div>

      {/* Category filter bar */}
      <div className="category-filter-bar">
        {VOW_CATEGORIES.map(cat => {
          const count = categoryCounts[cat.slug] ?? 0
          if (cat.slug !== 'all' && count === 0) return null
          return (
            <button
              key={cat.slug}
              className={`cat-filter-btn${activeCategory === cat.slug ? ' active' : ''}`}
              onClick={() => setActiveCategory(cat.slug)}
            >
              {cat.label}
              {count > 0 && <span className="cat-count">{count}</span>}
            </button>
          )
        })}
      </div>

      {filteredTemplates.length === 0 ? (
        <div className="templates-empty">
          {templates.length === 0
            ? <><p>NO TEMPLATES FOUND</p><p className="templates-empty-hint">Create templates via the Admin Panel.</p></>
            : <p>NO TEMPLATES IN THIS CATEGORY</p>
          }
        </div>
      ) : (
        <div className="templates-grid">
          {filteredTemplates.map(t => (
            <TemplateCard
              key={t.id}
              template={t}
              onEdit={() => handleEdit(t)}
              onDelete={() => handleDelete(t.id, t.name)}
              grabEvent={grabEvent}
              onMetaLoaded={handleMetaLoaded}
            />
          ))}
        </div>
      )}

      {editingTemplate && (
        <TemplateEditorModal
          template={editingTemplate}
          onSave={handleSave}
          onGrab={handleGrab}
          onClose={() => setEditingTemplate(null)}
        />
      )}
    </div>
  )
}
export default TemplatesGrid
