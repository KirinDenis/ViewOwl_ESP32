/**
 * @file api.js
 * @description Fetch wrapper that automatically attaches the JWT Bearer token
 * to every request and normalises error handling.
 *
 * Usage:
 *   const devices = await api.get("/api/devices");
 *   const created = await api.post("/api/devices", { name: "Living Room" });
 *   await api.delete(`/api/devices/${id}`);
 */

const api = (() => {
  /** @returns {string} The stored JWT token, or empty string. */
  function _token() {
    return localStorage.getItem(CONFIG.TOKEN_KEY) || "";
  }

  /**
   * Builds common request headers including Authorization when a token exists.
   * @returns {HeadersInit}
   */
  function _headers() {
    const h = { "Content-Type": "application/json" };
    const t = _token();
    if (t) h["Authorization"] = `Bearer ${t}`;
    return h;
  }

  /**
   * Core fetch wrapper. Throws a descriptive Error on non-2xx responses.
   * @param {string} path  API path relative to CONFIG.API_BASE_URL.
   * @param {RequestInit} [options]  Fetch options.
   * @returns {Promise<any>} Parsed JSON body, or null for 204 No Content.
   */
  async function _fetch(path, options = {}) {
    const url = CONFIG.API_BASE_URL + path;
    const res = await fetch(url, { ...options, headers: _headers() });

    if (res.status === 401) {
      // Token expired or invalid — force logout
      auth.logout();
      return null;
    }

    if (!res.ok) {
      const text = await res.text().catch(() => res.statusText);
      throw new Error(`[${res.status}] ${text}`);
    }

    if (res.status === 204) return null;
    return res.json();
  }

  return {
    /** GET request. */
    get: (path) => _fetch(path, { method: "GET" }),

    /** POST request with JSON body. */
    post: (path, body) =>
      _fetch(path, { method: "POST", body: JSON.stringify(body) }),

    /** PATCH request with JSON body. */
    patch: (path, body) =>
      _fetch(path, { method: "PATCH", body: JSON.stringify(body) }),

    /** PUT request with JSON body. */
    put: (path, body) =>
      _fetch(path, { method: "PUT", body: JSON.stringify(body) }),

    /** DELETE request. */
    delete: (path) => _fetch(path, { method: "DELETE" }),
  };
})();
