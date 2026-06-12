/**
 * @file auth.js
 * @description Login, logout, and JWT token storage helpers.
 */

const auth = (() => {
  /**
   * Attempts to log in with the given credentials.
   * On success, stores the token and redirects to dashboard.html.
   * On failure, returns the error message for display.
   *
   * @param {string} login
   * @param {string} password
   * @returns {Promise<string|null>} Error message, or null on success.
   */
  async function login(login, password) {
    try {
      const res = await fetch(CONFIG.API_BASE_URL + "/api/auth/login", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ login, password }),
      });

      if (!res.ok) {
        return "Invalid login or password.";
      }

      const data = await res.json();
      localStorage.setItem(CONFIG.TOKEN_KEY, data.token);

      // Cache the current user profile immediately after login
      const me = await fetch(CONFIG.API_BASE_URL + "/api/me", {
        headers: { Authorization: `Bearer ${data.token}` },
      });
      if (me.ok) {
        localStorage.setItem(CONFIG.USER_KEY, JSON.stringify(await me.json()));
      }

      window.location.href = "/dashboard";
      return null;
    } catch (err) {
      return "Connection error. Please try again.";
    }
  }

  /**
   * Clears stored credentials and redirects to the login page.
   */
  function logout() {
    localStorage.removeItem(CONFIG.TOKEN_KEY);
    localStorage.removeItem(CONFIG.USER_KEY);
    window.location.href = "/index.html";
  }

  /**
   * Returns the cached user profile, or null if not logged in.
   * @returns {{ id: number, login: string, role: string, sandboxEnabled: boolean }|null}
   */
  function currentUser() {
    const raw = localStorage.getItem(CONFIG.USER_KEY);
    if (!raw) return null;
    try {
      return JSON.parse(raw);
    } catch {
      return null;
    }
  }

  /**
   * Returns true when a token exists in storage.
   * Does NOT validate the token signature or expiry client-side —
   * the API will return 401 if the token is invalid.
   * @returns {boolean}
   */
  function isLoggedIn() {
    return !!localStorage.getItem(CONFIG.TOKEN_KEY);
  }

  return { login, logout, currentUser, isLoggedIn };
})();
