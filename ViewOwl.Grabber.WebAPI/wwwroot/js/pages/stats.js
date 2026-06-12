/**
 * @file pages/stats.js
 * @description Device statistics page — uptime ratio + recent ping history.
 * Navigated to via router.navigate("stats?deviceId=42") from the devices page.
 */

const statsPage = (() => {
  async function init() {
    const container = document.getElementById("page-content");

    // Read deviceId from hash query params: #stats?deviceId=42
    const deviceId = router.params().deviceId;

    if (!deviceId) {
      container.innerHTML = `
        <div class="container-fluid p-0 page-anim">
          <div class="alert alert-info">
            <i class="fas fa-info-circle me-2"></i>
            Select a device from the
            <a href="#" onclick="router.navigate('devices'); return false;">Devices</a>
            page to view its statistics.
          </div>
        </div>`;
      return;
    }

    let stats;
    try {
      stats = await api.get(`/api/devices/${deviceId}/stats`);
    } catch (err) {
      container.innerHTML = `<div class="alert alert-danger m-3">${err.message}</div>`;
      return;
    }

    _render(stats);
  }

  function _render(stats) {
    const uptimePct = stats.uptimeRatio !== null
      ? (stats.uptimeRatio * 100).toFixed(1) + "%"
      : "No data";

    const uptimeColor = stats.uptimeRatio === null ? "secondary"
      : stats.uptimeRatio >= 0.95 ? "success"
      : stats.uptimeRatio >= 0.80 ? "warning"
      : "danger";

    const pingsHtml = (stats.recentPings || []).map((p) => `
      <tr>
        <td class="small">${new Date(p.timestamp).toLocaleString()}</td>
        <td>
          <span class="badge ${p.success ? "bg-success" : "bg-danger"}">
            ${p.success ? "OK" : "FAIL"}
          </span>
        </td>
      </tr>`).join("") ||
      `<tr><td colspan="2" class="text-center text-muted py-3">No pings recorded.</td></tr>`;

    const successCount = (stats.recentPings || []).filter((p) => p.success).length;
    const totalCount   = stats.recentPings?.length ?? 0;

    document.getElementById("page-content").innerHTML = `
      <div class="container-fluid p-0 page-anim">
        <div class="d-flex align-items-center mb-3">
          <button class="btn btn-sm btn-outline-secondary me-3"
                  onclick="router.navigate('devices'); return false;">
            <i class="fas fa-arrow-left me-1"></i>Back
          </button>
          <h1 class="h3 mb-0">
            Device Stats
            <small class="text-muted fs-5">#${stats.deviceId}</small>
          </h1>
        </div>

        <!-- Stat cards -->
        <div class="row g-3 mb-3">

          <div class="col-sm-6 col-xl-3">
            <div class="card h-100">
              <div class="card-body">
                <div class="d-flex justify-content-between align-items-start">
                  <div>
                    <h6 class="card-title text-muted mb-1 text-uppercase small fw-semibold">
                      24h Uptime
                    </h6>
                    <h2 class="mb-0 text-${uptimeColor}">${uptimePct}</h2>
                  </div>
                  <div class="stat-icon text-${uptimeColor} ms-3 opacity-75">
                    <i class="fas fa-wifi"></i>
                  </div>
                </div>
              </div>
            </div>
          </div>

          <div class="col-sm-6 col-xl-3">
            <div class="card h-100">
              <div class="card-body">
                <div class="d-flex justify-content-between align-items-start">
                  <div>
                    <h6 class="card-title text-muted mb-1 text-uppercase small fw-semibold">
                      Pings recorded
                    </h6>
                    <h2 class="mb-0">${totalCount}</h2>
                  </div>
                  <div class="stat-icon text-primary ms-3 opacity-75">
                    <i class="fas fa-list"></i>
                  </div>
                </div>
              </div>
            </div>
          </div>

          <div class="col-sm-6 col-xl-3">
            <div class="card h-100">
              <div class="card-body">
                <div class="d-flex justify-content-between align-items-start">
                  <div>
                    <h6 class="card-title text-muted mb-1 text-uppercase small fw-semibold">
                      Successful
                    </h6>
                    <h2 class="mb-0 text-success">${successCount}</h2>
                  </div>
                  <div class="stat-icon text-success ms-3 opacity-75">
                    <i class="fas fa-check-circle"></i>
                  </div>
                </div>
              </div>
            </div>
          </div>

          <div class="col-sm-6 col-xl-3">
            <div class="card h-100">
              <div class="card-body">
                <div class="d-flex justify-content-between align-items-start">
                  <div>
                    <h6 class="card-title text-muted mb-1 text-uppercase small fw-semibold">
                      Failed
                    </h6>
                    <h2 class="mb-0 text-danger">${totalCount - successCount}</h2>
                  </div>
                  <div class="stat-icon text-danger ms-3 opacity-75">
                    <i class="fas fa-times-circle"></i>
                  </div>
                </div>
              </div>
            </div>
          </div>

        </div>

        <!-- Recent ping history -->
        <div class="row">
          <div class="col-12">
            <div class="card">
              <div class="card-header">
                <h5 class="card-title mb-0">Recent ping history</h5>
              </div>
              <div class="card-body p-0">
                <div class="table-responsive">
                  <table class="table table-hover mb-0">
                    <thead class="table-light">
                      <tr><th>Timestamp</th><th>Status</th></tr>
                    </thead>
                    <tbody>${pingsHtml}</tbody>
                  </table>
                </div>
              </div>
            </div>
          </div>
        </div>

      </div>`;
  }

  return { init };
})();
