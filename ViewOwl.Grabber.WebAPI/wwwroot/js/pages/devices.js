/**
 * @file pages/devices.js
 * @description Devices list page — shows all devices owned by the current user
 * with register / delete / assign-template / stats actions.
 *
 * Pattern: init → fetch devices + templates in parallel → render
 */

const devicesPage = (() => {
  /** Cache of templates for the dropdown (refreshed on every init). */
  let _templates = [];

  // ── Lifecycle ─────────────────────────────────────────────────────────────

  /** Renders the page into #page-content. */
  async function init() {
    const container = document.getElementById("page-content");

    let devices, templates;
    try {
      [devices, templates] = await Promise.all([
        api.get("/api/devices"),
        api.get("/api/templates")
      ]);
    } catch (err) {
      container.innerHTML = `<div class="alert alert-danger m-3">${err.message}</div>`;
      return;
    }

    _templates = templates || [];
    container.innerHTML = _skeleton();
    _render(devices || []);
    _bindEvents();
  }

  // ── HTML builders ─────────────────────────────────────────────────────────

  function _skeleton() {
    return `
      <div class="container-fluid p-0 page-anim">
        <div class="d-flex justify-content-between align-items-center mb-3">
          <h1 class="h3 mb-0">Devices</h1>
          <button class="btn btn-primary btn-sm" id="btn-add-device">
            <i class="fas fa-plus me-1"></i> Register device
          </button>
        </div>
        <div class="row">
          <div class="col-12">
            <div class="card">
              <div class="card-header">
                <h5 class="card-title mb-0">Registered devices</h5>
              </div>
              <div class="card-body p-0">
                <div class="table-responsive">
                  <table class="table table-hover mb-0" id="devices-table">
                    <thead class="table-light">
                      <tr>
                        <th>Name</th>
                        <th>Token</th>
                        <th>Active template</th>
                        <th>Assign template</th>
                        <th>Created</th>
                        <th></th>
                      </tr>
                    </thead>
                    <tbody id="devices-tbody"></tbody>
                  </table>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>`;
  }

  function _render(devices) {
    const tbody = document.getElementById("devices-tbody");
    if (!devices.length) {
      tbody.innerHTML = `
        <tr>
          <td colspan="6" class="text-center text-muted py-5">
            <i class="fas fa-tv fa-2x mb-2 d-block opacity-50"></i>
            No devices registered yet.
          </td>
        </tr>`;
      return;
    }
    tbody.innerHTML = devices.map(_row).join("");
  }

  function _row(d) {
    const activeName = _templates.find((t) => t.id === d.activeTemplateId)?.name ?? "—";

    const templateOptions = [
      `<option value="">— select —</option>`,
      ..._templates.map((t) =>
        `<option value="${t.id}" ${t.id === d.activeTemplateId ? "selected" : ""}>${_esc(t.name)}</option>`)
    ].join("");

    return `
      <tr data-id="${d.id}">
        <td class="align-middle fw-semibold">${_esc(d.name)}</td>
        <td class="align-middle">
          <code class="text-muted small">${_esc(d.token)}</code>
          <button class="btn btn-sm btn-outline-secondary ms-1 btn-copy-token"
                  data-token="${_esc(d.token)}" title="Copy token"
                  style="padding:1px 6px;font-size:.7rem;line-height:1.4;">
            <i class="fas fa-copy"></i>
          </button>
        </td>
        <td class="align-middle">
          <span class="badge bg-secondary" id="active-tpl-${d.id}">${_esc(activeName)}</span>
        </td>
        <td class="align-middle" style="min-width:220px;">
          <div class="input-group input-group-sm">
            <select class="form-select form-select-sm tpl-select" data-device-id="${d.id}">
              ${templateOptions}
            </select>
            <button class="btn btn-outline-primary btn-assign-template" data-device-id="${d.id}">
              Assign
            </button>
          </div>
        </td>
        <td class="align-middle text-muted small">${new Date(d.createdAt).toLocaleDateString()}</td>
        <td class="align-middle text-end" style="white-space:nowrap;">
          <button class="btn btn-sm btn-info me-1 btn-stats"
                  data-device-id="${d.id}" title="View stats">
            <i class="fas fa-chart-bar"></i>
          </button>
          <button class="btn btn-sm btn-outline-danger btn-delete-device" data-id="${d.id}"
                  title="Delete device">
            <i class="fas fa-trash"></i>
          </button>
        </td>
      </tr>`;
  }

  // ── Event bindings ────────────────────────────────────────────────────────

  function _bindEvents() {
    // Register new device
    document.getElementById("btn-add-device")?.addEventListener("click", async () => {
      const name = await dialog.prompt("New device name", "e.g. Living Room Display");
      if (!name?.trim()) return;
      try {
        await api.post("/api/devices", { name: name.trim() });
        toastr.success("Device registered.");
        await init();
      } catch (err) {
        toastr.error("Error: " + err.message);
      }
    });

    const tbody = document.getElementById("devices-tbody");

    // Delete device
    tbody?.addEventListener("click", async (e) => {
      const delBtn = e.target.closest(".btn-delete-device");
      if (!delBtn) return;
      const confirmed = await dialog.confirm("Delete this device and all its data?");
      if (!confirmed) return;
      try {
        await api.delete(`/api/devices/${delBtn.dataset.id}`);
        toastr.success("Device deleted.");
        await init();
      } catch (err) {
        toastr.error("Error: " + err.message);
      }
    });

    // Assign template
    tbody?.addEventListener("click", async (e) => {
      const assignBtn = e.target.closest(".btn-assign-template");
      if (!assignBtn) return;

      const deviceId  = assignBtn.dataset.deviceId;
      const row       = assignBtn.closest("tr");
      const select    = row?.querySelector(".tpl-select");
      const templateId = select?.value;

      if (!templateId) {
        toastr.warning("Select a template first.");
        return;
      }

      assignBtn.disabled = true;
      try {
        await api.post(`/api/devices/${deviceId}/template/${templateId}`, {});
        const tplName = _templates.find((t) => t.id === parseInt(templateId))?.name ?? templateId;
        const badge   = document.getElementById(`active-tpl-${deviceId}`);
        if (badge) badge.textContent = tplName;
        toastr.success("Template assigned.");
      } catch (err) {
        toastr.error("Error: " + err.message);
      } finally {
        assignBtn.disabled = false;
      }
    });

    // Copy token to clipboard
    tbody?.addEventListener("click", async (e) => {
      const copyBtn = e.target.closest(".btn-copy-token");
      if (!copyBtn) return;
      const token = copyBtn.dataset.token;
      try {
        if (navigator.clipboard) {
          await navigator.clipboard.writeText(token);
        } else {
          // Fallback for HTTP (non-secure) contexts where clipboard API is unavailable
          const ta = document.createElement("textarea");
          ta.value = token;
          ta.style.position = "fixed";
          ta.style.opacity = "0";
          document.body.appendChild(ta);
          ta.focus();
          ta.select();
          document.execCommand("copy");
          document.body.removeChild(ta);
        }
        toastr.success("Token copied to clipboard.");
      } catch {
        toastr.error("Could not copy to clipboard.");
      }
    });

    // Navigate to Stats page with deviceId in the hash
    tbody?.addEventListener("click", (e) => {
      const statsBtn = e.target.closest(".btn-stats");
      if (!statsBtn) return;
      router.navigate(`stats?deviceId=${statsBtn.dataset.deviceId}`);
    });
  }

  // ── Utilities ─────────────────────────────────────────────────────────────

  /** Minimal HTML escape for user-supplied strings. */
  function _esc(str) {
    return String(str)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  return { init };
})();
