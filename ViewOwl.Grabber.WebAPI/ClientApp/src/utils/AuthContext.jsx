import { createContext, useContext, useState, useEffect } from 'react'
import { authFetch } from './auth'

/// Context value shape: { currentUser: object|null, isAdmin: bool }
const AuthContext = createContext({ currentUser: null, isAdmin: false })

/// Wraps the app and fetches /api/me once on mount.
/// All components can call useAuth() instead of prop-drilling role checks.
export function AuthProvider({ children }) {
  const [currentUser, setCurrentUser] = useState(null)

  useEffect(() => {
    authFetch('/api/me')
      .then(r => (r?.ok ? r.json() : null))
      .then(u => { if (u) setCurrentUser(u) })
      .catch(() => {})
  }, [])

  const isAdmin = currentUser?.role === 'Admin'

  return (
    <AuthContext.Provider value={{ currentUser, isAdmin }}>
      {children}
    </AuthContext.Provider>
  )
}

/// Returns { currentUser, isAdmin } from the nearest AuthProvider.
export const useAuth = () => useContext(AuthContext)
