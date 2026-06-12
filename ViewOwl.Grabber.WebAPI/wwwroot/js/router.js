/**
 * @file router.js
 * @description Minimal hash-router. Maps URL fragments to page modules and
 * renders them into #page-content with an animated transition.
 *
 * Supports query params in the hash: #route?key=value&key2=value2
 *
 * Registration:
 *   router.register("devices", devicesPage);
 *
 * Navigation:
 *   router.navigate("devices");             // plain route
 *   router.navigate("stats?deviceId=42");   // route with params
 *   window.location.hash = "#stats?deviceId=42";  // also works
 *
 * Reading params inside a page module:
 *   const { deviceId } = router.params();
 */

const router = (() => {
  /** @type {Map<string, { init: () => Promise<void> }>} */
  const routes = new Map();

  /** Currently active route name. */
  let _current = null;

  /** Query-string parameters parsed from the current hash. */
  let _params = {};

  // ── Internal helpers ──────────────────────────────────────────────────────

  /**
   * Parses window.location.hash into a route name and a params map.
   * e.g. "#stats?deviceId=42" → { route: "stats", params: { deviceId: "42" } }
   * @returns {{ route: string, params: Record<string, string> }}
   */
  function _parseHash() {
    const raw        = window.location.hash.replace(/^#/, "");
    const questionIdx = raw.indexOf("?");
    const route      = questionIdx === -1 ? raw : raw.slice(0, questionIdx);
    const query      = questionIdx === -1 ? "" : raw.slice(questionIdx + 1);
    const params     = {};

    if (query) {
      query.split("&").forEach((pair) => {
        const eqIdx = pair.indexOf("=");
        if (eqIdx === -1) return;
        const key = decodeURIComponent(pair.slice(0, eqIdx));
        const val = decodeURIComponent(pair.slice(eqIdx + 1));
        if (key) params[key] = val;
      });
    }

    return { route, params };
  }

  /**
   * Updates the AdminKit sidebar: adds .active to the matching .sidebar-item.
   * @param {string} routeName
   */
  function _updateSidebar(routeName) {
    document.querySelectorAll(".sidebar-item[data-nav-route]").forEach((item) => {
      item.classList.toggle("active", item.dataset.navRoute === routeName);
    });
  }

  // ── Public API ────────────────────────────────────────────────────────────

  /**
   * Registers a page module under the given hash name.
   * @param {string} name   Hash fragment without '#' or '?'.
   * @param {{ init: () => Promise<void> }} page  Page module with an init() function.
   */
  function register(name, page) {
    routes.set(name, page);
  }

  /**
   * Returns a copy of the query-string params parsed from the current hash.
   * @returns {Record<string, string>}
   */
  function params() {
    return { ..._params };
  }

  /**
   * Renders the page corresponding to the current window.location.hash.
   * Shows a spinner while the page module initialises, then fades the
   * new content in with the .page-anim CSS animation.
   * Falls back to the first registered route when the hash is empty or unknown.
   */
  async function render() {
    const { route, params: parsedParams } = _parseHash();
    const name = route || routes.keys().next().value;
    const page = routes.get(name);

    if (!page) return;

    _current = name;
    _params  = parsedParams;

    _updateSidebar(name);

    // Show spinner while the page module fetches its data
    const container = document.getElementById("page-content");
    if (container) {
      container.innerHTML =
        '<div class="page-spinner">' +
          '<div class="spinner-border text-primary" role="status">' +
            '<span class="visually-hidden">Loading…</span>' +
          '</div>' +
        '</div>';
    }

    try {
      await page.init();
    } catch (err) {
      if (container) {
        container.innerHTML =
          `<div class="alert alert-danger m-3">Error loading page: ${err.message}</div>`;
      }
    }
  }

  /**
   * Navigates to the given route (with optional query string) by updating the hash.
   * @param {string} nameWithParams  e.g. "stats" or "stats?deviceId=42"
   */
  function navigate(nameWithParams) {
    window.location.hash = "#" + nameWithParams;
  }

  // Re-render on every hash change
  window.addEventListener("hashchange", render);

  return {
    register,
    navigate,
    render,
    params,
    get current() { return _current; }
  };
})();
