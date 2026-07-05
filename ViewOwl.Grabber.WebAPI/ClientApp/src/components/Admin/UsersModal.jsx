import { useState, useEffect, useCallback } from 'react'
import { authFetch } from '../../utils/auth'
import { confirm } from '../../utils/confirm'
import './UsersModal.css'

/**
 * Admin-only user management popup. Lists users, creates/deletes them, and toggles
 * the per-user sandbox flag. JWT auth (admin role) via authFetch — wired to
 * AdminUsersController (/api/admin/users).
 */
export default function UsersModal({ onClose }) {
  const [users, setUsers] = useState(null)   // null = loading
  const [error, setError] = useState(null)

  const [login, setLogin]       = useState('')
  const [password, setPassword] = useState('')
  const [role, setRole]         = useState('User')
  const [busy, setBusy]         = useState(false)
  const [formErr, setFormErr]   = useState(null)

  const load = useCallback(() => {
    authFetch('/api/admin/users')
      .then(r => { if (!r) return; if (!r.ok) throw new Error(`HTTP ${r.status}`); return r.json() })
      .then(d => { if (d) { setUsers(d); setError(null) } })
      .catch(e => setError(e.message))
  }, [])

  useEffect(() => { load() }, [load])

  useEffect(() => {
    const onKey = (e) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  const create = async () => {
    if (!login.trim() || !password) { setFormErr('login and password are required'); return }
    setBusy(true); setFormErr(null)
    try {
      const r = await authFetch('/api/admin/users', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ login: login.trim(), password, role, sandboxEnabled: true }),
      })
      if (!r) return
      if (r.status === 409) { setFormErr('login already exists'); return }
      if (!r.ok) { setFormErr(`create failed (HTTP ${r.status})`); return }
      setLogin(''); setPassword(''); setRole('User')
      load()
    } finally { setBusy(false) }
  }

  const del = async (u) => {
    const ok = await confirm(`Delete user "${u.login}"? This cannot be undone.`,
      { title: 'DELETE USER', danger: true, yes: 'DELETE' })
    if (!ok) return
    const r = await authFetch(`/api/admin/users/${u.id}`, { method: 'DELETE' })
    if (r && r.ok) load()
  }

  const toggleSandbox = async (u) => {
    const r = await authFetch(`/api/admin/users/${u.id}/sandbox`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ enabled: !u.sandboxEnabled }),
    })
    if (r && r.ok) load()
  }

  return (
    <div className="um-overlay" onClick={e => { if (e.target === e.currentTarget) onClose() }}>
      <div className="um-modal">
        <div className="um-header">
          <span className="um-title">USERS</span>
          <span className="um-grow" />
          <button className="um-close" onClick={onClose} aria-label="Close">✕</button>
        </div>

        <div className="um-body">
          {error && <div className="um-error">{error}</div>}
          <table className="um-table">
            <thead>
              <tr><th>LOGIN</th><th>ROLE</th><th>SANDBOX</th><th>CREATED</th><th aria-label="actions" /></tr>
            </thead>
            <tbody>
              {users === null && <tr><td colSpan="5" className="um-dim">loading…</td></tr>}
              {users && users.length === 0 && <tr><td colSpan="5" className="um-dim">no users</td></tr>}
              {users && users.map(u => (
                <tr key={u.id}>
                  <td className="um-login">{u.login}</td>
                  <td>{u.role === 'Admin'
                    ? <span className="um-badge-admin">ADMIN</span>
                    : <span className="um-dim">USER</span>}</td>
                  <td>
                    <button
                      className={`um-toggle${u.sandboxEnabled ? ' on' : ''}`}
                      onClick={() => toggleSandbox(u)}
                      title="Toggle sandbox isolation"
                    >{u.sandboxEnabled ? 'ON' : 'OFF'}</button>
                  </td>
                  <td className="um-dim">{new Date(u.createdAt).toISOString().slice(0, 10)}</td>
                  <td className="um-actions">
                    <button className="um-del" onClick={() => del(u)} title="Delete user">DELETE</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        <div className="um-create">
          <div className="um-create-title">ADD USER</div>
          <div className="um-create-row">
            <input className="um-input" placeholder="login" value={login}
                   onChange={e => setLogin(e.target.value)} autoComplete="off" />
            <input className="um-input" type="password" placeholder="password" value={password}
                   onChange={e => setPassword(e.target.value)} autoComplete="new-password" />
            <select className="um-select" value={role} onChange={e => setRole(e.target.value)}>
              <option value="User">USER</option>
              <option value="Admin">ADMIN</option>
            </select>
            <button className="um-add" onClick={create} disabled={busy}>ADD</button>
          </div>
          {formErr && <div className="um-form-err">{formErr}</div>}
        </div>
      </div>
    </div>
  )
}
