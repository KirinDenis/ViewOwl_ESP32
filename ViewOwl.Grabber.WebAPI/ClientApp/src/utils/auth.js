/**
 * Auth helpers — reads JWT from localStorage (shared with the Bootstrap admin panel).
 * The token is stored under "viewowl_token" by /js/auth.js.
 */

const TOKEN_KEY = 'viewowl_token'
const LOGIN_URL = '/login.html'

/** Returns the stored JWT token, or null if not logged in. */
export function getToken() {
  return localStorage.getItem(TOKEN_KEY)
}

/** Returns fetch headers with Authorization: Bearer <token> if a token exists. */
export function authHeaders(extra = {}) {
  const token = getToken()
  return token
    ? { Authorization: `Bearer ${token}`, ...extra }
    : { ...extra }
}

/**
 * Wraps fetch() with automatic Authorization header injection.
 * Redirects to /login.html on 401.
 */
export async function authFetch(url, options = {}) {
  const token = getToken()

  if (!token) {
    window.location.href = LOGIN_URL
    return null
  }

  const res = await fetch(url, {
    ...options,
    headers: {
      ...authHeaders(options.headers ?? {}),
    },
  })

  if (res.status === 401) {
    // Token expired or invalid — go back to login
    window.location.href = LOGIN_URL
    return null
  }

  return res
}
