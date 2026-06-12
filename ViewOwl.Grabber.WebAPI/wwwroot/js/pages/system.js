/**
 * @file pages/system.js
 * @description System status page — shows exchange-folder stats, auto-grab
 * sources from GrabbedList.json, and recent grab activity across all templates.
 * Visible to all authenticated users; admins see data for every user's templates.
 */

const systemPage = (() => {

  // ── SignalR event handlers (registered once at module level) ──────────────
  // Refresh the whole status view whenever any grab completes so the
  // recent-grabs table and storage stats stay current automatically.

  realtime.on("GrabCompleted", () => {
    // Only refresh when the system page is actually visible.
    if (document.getElementById("sys-stat-cards")) init();
  });

  // ── Lifecycle ─────────────────────────────────────────────────────────────

  async function init() {
    const container = document.getElementById("page-content");
    container.innerHTML = _skeleton();

    let status;
    try {
      status = await api.get("/api/system/status");
    } catch (err) {
      container.innerHTML = `<div class="alert alert-danger m-3">${err.message}</div>`;
      return;
    }

    _renderStats(status);
    _renderSources(status.sources);
    _renderGrabs(status.recentGrabs);
  }

  // ── Skeleton ──────────────────────────────────────────────────────────────

  function _skeleton() {
    return `
      <div class="container-fluid p-0 page-anim">
        <h1 class="h3 mb-3">System status</h1>

        <!-- Stat cards row -->
        <div class="row mb-3" id="sys-stat-cards"></div>

        <div class="row">

          <!-- Auto-grab sources -->
          <div class="col-12 col-xl-5 mb-3">
            <div class="card h-100">
              <div class="card-header">
                <h5 class="card-title mb-0">
                  <i class="fas fa-list-alt me-2 text-muted opacity-75"></i>Auto-grab sources
                </h5>
              </div>
              <div class="card-body p-0">
                <div class="table-responsive">
                  <table class="table table-hover mb-0 small">
                    <thead class="table-light">
                      <tr><th>Source URL / path</th><th>Size</th></tr>
                    </thead>
                    <tbody id="sys-sources-tbody"></tbody>
                  </table>
                </div>
              </div>
            </div>
          </div>

          <!-- Recent grabs -->
          <div class="col-12 col-xl-7 mb-3">
            <div class="card h-100">
              <div class="card-header">
                <h5 class="card-title mb-0">
                  <i class="fas fa-history me-2 text-muted opacity-75"></i>Recent grabs
                </h5>
              </div>
              <div class="card-body p-0">
                <div class="table-responsive">
                  <table class="table table-hover mb-0 small">
                    <thead class="table-light">
                      <tr>
                        <th>Template</th>
                        <th>Requested</th>
                        <th>Duration</th>
                        <th>Result</th>
                        <th>Message</th>
                      </tr>
                    </thead>
                    <tbody id="sys-grabs-tbody"></tbody>
                  </table>
                </div>
              </div>
            </div>
          </div>

        </div>
      </div>`;
  }

  // ── Renderers ─────────────────────────────────────────────────────────────

  function _renderStats(s) {
    const sizeStr = _fmtBytes(s.totalBinBytes);

    document.getElementById("sys-stat-cards").innerHTML = `
      <div class="col-sm-6 col-xl-3 mb-3">
        <div class="card">
          <div class="card-body">
            <div class="d-flex justify-content-between align-items-start">
              <div>
                <h6 class="card-title text-muted mb-1 text-uppercase small fw-semibold">Bin files</h6>
                <h2 class="mb-0">${s.binFileCount}</h2>
              </div>
              <div class="stat-icon text-primary ms-3 opacity-75"><i class="fas fa-file-archive"></i></div>
            </div>
          </div>
        </div>
      </div>
      <div class="col-sm-6 col-xl-3 mb-3">
        <div class="card">
          <div class="card-body">
            <div class="d-flex justify-content-between align-items-start">
              <div>
                <h6 class="card-title text-muted mb-1 text-uppercase small fw-semibold">Storage used</h6>
                <h2 class="mb-0">${sizeStr}</h2>
              </div>
              <div class="stat-icon text-warning ms-3 opacity-75"><i class="fas fa-hdd"></i></div>
            </div>
          </div>
        </div>
      </div>
      <div class="col-sm-6 col-xl-3 mb-3">
        <div class="card">
          <div class="card-body">
            <div class="d-flex justify-content-between align-items-start">
              <div>
                <h6 class="card-title text-muted mb-1 text-uppercase small fw-semibold">Grab sources</h6>
                <h2 class="mb-0">${s.sources.length}</h2>
              </div>
              <div class="stat-icon text-success ms-3 opacity-75"><i class="fas fa-rss"></i></div>
            </div>
          </div>
        </div>
      </div>
      <div class="col-sm-6 col-xl-3 mb-3">
        <div class="card">
          <div class="card-body">
            <div class="d-flex justify-content-between align-items-start">
              <div>
                <h6 class="card-title text-muted mb-1 text-uppercase small fw-semibold">Recent grabs</h6>
                <h2 class="mb-0">${s.recentGrabs.length}</h2>
              </div>
              <div class="stat-icon text-info ms-3 opacity-75"><i class="fas fa-camera"></i></div>
            </div>
          </div>
        </div>
      </div>`;
  }

  function _renderSources(sources) {
    const tbody = document.getElementById("sys-sources-tbody");
    if (!sources.length) {
      tbody.innerHTML =
        `<tr><td colspan="2" class="text-center text-muted py-3">No sources configured.</td></tr>`;
      return;
    }
    tbody.innerHTML = sources.map((s) => `
      <tr>
        <td class="text-break">${_esc(s.source)}</td>
        <td class="text-nowrap text-muted">${s.width}×${s.height}</td>
      </tr>`).join("");
  }

  function _renderGrabs(grabs) {
    const tbody = document.getElementById("sys-grabs-tbody");
    if (!grabs.length) {
      tbody.innerHTML =
        `<tr><td colspan="5" class="text-center text-muted py-3">No grab activity yet.</td></tr>`;
      return;
    }
    tbody.innerHTML = grabs.map((g) => {
      const badge = g.success === null || g.success === undefined
        ? `<span class="badge bg-warning text-dark">Pending</span>`
        : g.success
          ? `<span class="badge bg-success">OK</span>`
          : `<span class="badge bg-danger">Failed</span>`;

      const duration = (g.completedAt && g.requestedAt)
        ? _fmtMs(new Date(g.completedAt) - new Date(g.requestedAt))
        : "—";

      return `
        <tr>
          <td class="fw-semibold">${_esc(g.templateName)}</td>
          <td class="text-muted text-nowrap">${new Date(g.requestedAt).toLocaleString()}</td>
          <td class="text-muted text-nowrap">${duration}</td>
          <td>${badge}</td>
          <td class="text-muted text-truncate" style="max-width:200px" title="${_esc(g.message || "")}">${_esc(g.message || "")}</td>
        </tr>`;
    }).join("");
  }

  // ── Utilities ─────────────────────────────────────────────────────────────

  function _esc(str) {
    return String(str)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function _fmtBytes(bytes) {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
  }

  function _fmtMs(ms) {
    if (ms < 1000) return `${ms}ms`;
    return `${(ms / 1000).toFixed(1)}s`;
  }

  return { init };
})();
