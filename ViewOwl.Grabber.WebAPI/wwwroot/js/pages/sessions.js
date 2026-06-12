/**
 * sessions.js — "My Sessions" dashboard page.
 * Shows all active sessions for the current user, lets them revoke individual
 * sessions or log out all other devices with one click.
 */
const sessionsPage = (() => {

  // ── Helpers ────────────────────────────────────────────────────────────────

  function esc(s) {
    return String(s ?? '').replace(/[&<>"']/g, c =>
      ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  function fmtDate(iso) {
    if (!iso) return '—';
    return new Date(iso).toLocaleString(undefined, {
      year: 'numeric', month: 'short', day: 'numeric',
      hour: '2-digit', minute: '2-digit'
    });
  }

  function uaIcon(ua) {
    const u = (ua || '').toLowerCase();
    if (u.includes('mobile') || u.includes('android') || u.includes('iphone')) return '📱';
    if (u.includes('tablet') || u.includes('ipad'))  return '📟';
    return '🖥️';
  }

  // ── Render ─────────────────────────────────────────────────────────────────

  async function render() {
    document.getElementById('page-content').innerHTML = `
      <div class="container-fluid p-0 page-anim">
        <div class="row mb-3">
          <div class="col">
            <h1 class="h3 mb-0">My Sessions</h1>
            <p class="text-muted small mb-0">Active login sessions for your account</p>
          </div>
          <div class="col-auto d-flex align-items-center gap-2">
            <button class="btn btn-sm btn-outline-danger" id="btn-revoke-all">
              <i class="fas fa-sign-out-alt me-1"></i>Log out all other devices
            </button>
            <button class="btn btn-sm btn-outline-secondary" id="btn-sessions-refresh">
              <i class="fas fa-sync-alt me-1"></i>Refresh
            </button>
          </div>
        </div>

        <div class="row">
          <div class="col-12">
            <div class="card">
              <div class="card-body p-0">
                <div id="sessions-list">
                  <div class="p-4 text-center text-muted">
                    <div class="spinner-border spinner-border-sm me-2" role="status"></div>
                    Loading sessions…
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>

        <div class="row mt-3">
          <div class="col-12">
            <div class="card border-0 bg-light">
              <div class="card-body py-2 px-3">
                <small class="text-muted">
                  <i class="fas fa-info-circle me-1"></i>
                  Sessions are identified by IP address and browser fingerprint.
                  Revoking a session does not immediately disconnect it — the token
                  remains valid until it expires, but the session will be flagged
                  and refused on the next fingerprint check.
                </small>
              </div>
            </div>
          </div>
        </div>
      </div>`;

    document.getElementById('btn-sessions-refresh')
      .addEventListener('click', loadSessions);
    document.getElementById('btn-revoke-all')
      .addEventListener('click', revokeAll);

    await loadSessions();
  }

  // ── Data ───────────────────────────────────────────────────────────────────

  async function loadSessions() {
    const list = document.getElementById('sessions-list');
    list.innerHTML = `<div class="p-4 text-center text-muted">
      <div class="spinner-border spinner-border-sm me-2" role="status"></div>
      Loading…</div>`;

    const sessions = await api.get('/api/me/sessions');

    if (!sessions || !sessions.length) {
      list.innerHTML = `<div class="p-4 text-center text-muted">
        <i class="fas fa-ghost fa-2x mb-2 d-block"></i>
        No active sessions found.<br>
        <small>Sessions are recorded when you log in. If you just signed in,
        try refreshing.</small>
      </div>`;
      return;
    }

    list.innerHTML = `<table class="table table-hover mb-0">
      <thead>
        <tr>
          <th class="ps-3">Device</th>
          <th>IP Address</th>
          <th>First seen</th>
          <th>Last seen</th>
          <th>Status</th>
          <th class="pe-3"></th>
        </tr>
      </thead>
      <tbody>
        ${sessions.map(s => sessionRow(s)).join('')}
      </tbody>
    </table>`;

    // Bind revoke buttons
    list.querySelectorAll('[data-revoke-session]').forEach(btn => {
      btn.addEventListener('click', () => revokeOne(btn.dataset.revokeSession));
    });
  }

  function sessionRow(s) {
    const icon      = uaIcon(s.userAgent);
    const isCurrent = s.isCurrent;
    const uaShort   = (s.userAgent || 'Unknown browser').slice(0, 80);

    return `<tr class="${isCurrent ? 'table-primary' : ''}">
      <td class="ps-3">
        <span class="me-2" style="font-size:1.2rem">${icon}</span>
        <span title="${esc(s.userAgent)}">${esc(uaShort)}${s.userAgent?.length > 80 ? '…' : ''}</span>
      </td>
      <td><code>${esc(s.ipAddress)}</code></td>
      <td class="text-muted small">${fmtDate(s.createdAt)}</td>
      <td class="text-muted small">${fmtDate(s.lastSeenAt)}</td>
      <td>
        ${isCurrent
          ? '<span class="badge bg-success"><i class="fas fa-check me-1"></i>Current</span>'
          : '<span class="badge bg-secondary">Active</span>'}
      </td>
      <td class="pe-3 text-end">
        ${isCurrent
          ? '<span class="text-muted small">—</span>'
          : `<button class="btn btn-sm btn-outline-danger"
                    data-revoke-session="${esc(s.sessionId)}"
                    title="Revoke this session">
               <i class="fas fa-times"></i>
             </button>`}
      </td>
    </tr>`;
  }

  async function revokeOne(sessionId) {
    const ok = await dialog.confirm(
      'Revoke this session? The device will be logged out on next request.',
      'Revoke');
    if (!ok) return;

    await api.delete(`/api/me/sessions/${encodeURIComponent(sessionId)}`);
    toastr.success('Session revoked');
    await loadSessions();
  }

  async function revokeAll() {
    const ok = await dialog.confirm(
      'Log out all other devices? Your current session will remain active.',
      'Log out others');
    if (!ok) return;

    await api.delete('/api/me/sessions');
    toastr.success('All other sessions revoked');
    await loadSessions();
  }

  // Router calls page.init() — alias to render so naming is consistent
  // with all other page modules in this project.
  return { init: render, render };
})();
