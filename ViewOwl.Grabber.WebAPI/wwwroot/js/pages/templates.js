/**
 * @file pages/templates.js
 * @description Templates list page — HTML template management with:
 *   • CodeMirror-based create / edit modal (monokai, htmlmixed, Ctrl+S)
 *   • Live HTML preview in a sandboxed iframe (fetches full content via GET /{id})
 *   • "Open in browser" action — writes the HTML into a new tab
 *   • On-demand screenshot grab via POST /{id}/grab
 *   • Grab log panel updated in real-time via SignalR (GrabStarted / GrabCompleted)
 *   • Template list kept in sync via SignalR (TemplateCreated / TemplateUpdated / TemplateDeleted)
 *   • 5-second poll fallback while SignalR is unavailable
 */

const templatesPage = (() => {
  /** Active poll timer handle; cleared when a grab completes or page unloads. */
  let _pollTimer = null;

  /** Currently open grab-log panel template id. */
  let _activeId = null;

  /**
   * Shared CodeMirror instance for the template editor modal.
   * Created once on first open and reused on every subsequent call.
   */
  let _cmEditor = null;

  const user = auth.currentUser();

  // ── SignalR real-time event handlers (registered once at module level) ────
  // Guard with DOM-presence checks so these are no-ops when the page is not active.

  realtime.on("TemplateCreated", (dto) => {
    const tbody = document.getElementById("templates-tbody");
    if (!tbody) return;
    // Remove the "no templates" placeholder if present
    const empty = tbody.querySelector("td[colspan]");
    if (empty) tbody.innerHTML = "";
    tbody.insertAdjacentHTML("beforeend", _row(dto));
  });

  realtime.on("TemplateUpdated", (dto) => {
    const row = document.querySelector(`#templates-tbody tr[data-id="${dto.id}"]`);
    if (!row) return;
    const nameCell = row.querySelector("td.align-middle.fw-semibold");
    if (nameCell) nameCell.textContent = dto.name;
    // Update data-name on all buttons in this row for consistency
    row.querySelectorAll("[data-name]").forEach((el) => (el.dataset.name = _esc(dto.name)));
  });

  realtime.on("TemplateDeleted", (id) => {
    const row = document.querySelector(`#templates-tbody tr[data-id="${id}"]`);
    if (!row) return;
    row.remove();
    const tbody = document.getElementById("templates-tbody");
    if (tbody && !tbody.children.length) {
      tbody.innerHTML = `
        <tr>
          <td colspan="3" class="text-center text-muted py-5">
            <i class="fas fa-file-code fa-2x mb-2 d-block opacity-50"></i>
            No templates yet. Click "New template" to create one.
          </td>
        </tr>`;
    }
  });

  realtime.on("GrabStarted", (log) => {
    if (String(log.templateId) !== String(_activeId)) return;
    const badge = document.getElementById("grab-status-badge");
    if (badge) { badge.textContent = "Pending…"; badge.className = "badge bg-warning text-dark"; }
  });

  realtime.on("GrabCompleted", async (log) => {
    if (String(log.templateId) !== String(_activeId)) return;
    const wrap = document.getElementById("grab-card-wrap");
    if (!wrap || wrap.classList.contains("d-none")) return;

    _stopPolling(); // SignalR delivered the result — no need to keep polling
    await _refreshGrabLog(_activeId);

    if (log.success) {
      toastr.success("Grab completed successfully!");
    } else {
      toastr.error("Grab failed: " + (log.message || "Unknown error"));
    }
  });

  // ── Lifecycle ─────────────────────────────────────────────────────────────

  async function init() {
    _stopPolling();
    _activeId = null;

    const container = document.getElementById("page-content");

    let templates;
    try {
      templates = await api.get("/api/templates");
    } catch (err) {
      container.innerHTML = `<div class="alert alert-danger m-3">${err.message}</div>`;
      return;
    }

    container.innerHTML = _skeleton();
    _render(templates || []);
    _bindEvents();
  }

  // ── HTML builders ─────────────────────────────────────────────────────────

  function _skeleton() {
    return `
      <div class="container-fluid p-0 page-anim">
        <div class="d-flex justify-content-between align-items-center mb-3">
          <h1 class="h3 mb-0">Templates</h1>
          <button class="btn btn-primary btn-sm" id="btn-add-template">
            <i class="fas fa-plus me-1"></i> New template
          </button>
        </div>
        <div class="row">

          <!-- Template list -->
          <div class="col-12">
            <div class="card">
              <div class="card-header">
                <h5 class="card-title mb-0">HTML templates</h5>
              </div>
              <div class="card-body p-0">
                <div class="table-responsive">
                  <table class="table table-hover mb-0">
                    <thead class="table-light">
                      <tr><th>Name</th><th>Created</th><th></th></tr>
                    </thead>
                    <tbody id="templates-tbody"></tbody>
                  </table>
                </div>
              </div>
            </div>
          </div>

          <!-- HTML preview panel (hidden by default) -->
          <div class="col-12 d-none" id="preview-card-wrap">
            <div class="card" id="preview-card">
              <div class="card-header d-flex justify-content-between align-items-center">
                <h5 class="card-title mb-0" id="preview-title">Preview</h5>
                <button class="btn btn-sm btn-outline-secondary" id="btn-close-preview">
                  <i class="fas fa-times me-1"></i>Close
                </button>
              </div>
              <div class="card-body text-center">
                <iframe id="preview-iframe"
                  style="width:480px;height:320px;border:1px solid var(--bs-border-color);display:block;margin:auto;border-radius:4px;"
                  ${user?.sandboxEnabled ? 'sandbox="allow-same-origin"' : ""}></iframe>
              </div>
            </div>
          </div>

          <!-- Grab log + preview image panel (hidden by default) -->
          <div class="col-12 d-none" id="grab-card-wrap">
            <div class="card" id="grab-card">
              <div class="card-header d-flex justify-content-between align-items-center">
                <h5 class="card-title mb-0">Grab log</h5>
                <span class="badge bg-secondary" id="grab-status-badge">Idle</span>
              </div>
              <div class="card-body">
                <!-- Last successful preview image -->
                <div id="grab-preview-wrap" class="text-center mb-3 d-none">
                  <img id="grab-preview-img"
                    style="max-width:480px;height:auto;border:1px solid var(--bs-border-color);border-radius:4px;"
                    alt="Last successful preview" />
                </div>
                <!-- Log entries table -->
                <div class="table-responsive">
                  <table class="table table-sm table-hover">
                    <thead class="table-light">
                      <tr>
                        <th>Requested</th>
                        <th>Completed</th>
                        <th>Status</th>
                        <th>Message</th>
                        <th>Size</th>
                      </tr>
                    </thead>
                    <tbody id="grab-log-tbody"></tbody>
                  </table>
                </div>
              </div>
            </div>
          </div>

        </div>
      </div>`;
  }

  function _render(templates) {
    const tbody = document.getElementById("templates-tbody");
    if (!templates.length) {
      tbody.innerHTML = `
        <tr>
          <td colspan="3" class="text-center text-muted py-5">
            <i class="fas fa-file-code fa-2x mb-2 d-block opacity-50"></i>
            No templates yet. Click "New template" to create one.
          </td>
        </tr>`;
      return;
    }
    tbody.innerHTML = templates.map(_row).join("");
  }

  function _row(t) {
    return `
      <tr data-id="${t.id}">
        <td class="align-middle fw-semibold">${_esc(t.name)}</td>
        <td class="align-middle text-muted small">${new Date(t.createdAt).toLocaleDateString()}</td>
        <td class="align-middle text-end" style="white-space:nowrap;">
          <button class="btn btn-sm btn-info me-1 btn-preview"
                  data-id="${t.id}" data-name="${_esc(t.name)}">
            <i class="fas fa-eye me-1"></i>Preview
          </button>
          <button class="btn btn-sm btn-outline-info me-1 btn-open-browser"
                  data-id="${t.id}" title="Open in browser tab">
            <i class="fas fa-external-link-alt"></i>
          </button>
          <button class="btn btn-sm btn-success me-1 btn-grab"
                  data-id="${t.id}" data-name="${_esc(t.name)}">
            <i class="fas fa-camera me-1"></i>Grab Now
          </button>
          <button class="btn btn-sm btn-secondary me-1 btn-show-log"
                  data-id="${t.id}" data-name="${_esc(t.name)}">
            <i class="fas fa-list me-1"></i>Log
          </button>
          <button class="btn btn-sm btn-outline-secondary me-1 btn-edit-template"
                  data-id="${t.id}" title="Edit template">
            <i class="fas fa-edit"></i>
          </button>
          <button class="btn btn-sm btn-outline-danger btn-delete-template" data-id="${t.id}">
            <i class="fas fa-trash"></i>
          </button>
        </td>
      </tr>`;
  }

  // ── Event bindings ────────────────────────────────────────────────────────

  function _bindEvents() {
    document.getElementById("btn-add-template")
      ?.addEventListener("click", _showCreateDialog);

    document.getElementById("btn-close-preview")
      ?.addEventListener("click", () => {
        document.getElementById("preview-card-wrap")?.classList.add("d-none");
      });

    document.querySelector("#templates-tbody")
      ?.addEventListener("click", async (e) => {
        const delBtn = e.target.closest(".btn-delete-template");
        if (delBtn) {
          const confirmed = await dialog.confirm("Delete this template and all its grab history?");
          if (!confirmed) return;
          try {
            await api.delete(`/api/templates/${delBtn.dataset.id}`);
            toastr.success("Template deleted.");
            _stopPolling();
            // Row removal handled by TemplateDeleted SignalR event;
            // call init() as fallback if SignalR is not connected.
          } catch (err) {
            toastr.error("Error: " + err.message);
          }
          return;
        }

        const editBtn = e.target.closest(".btn-edit-template");
        if (editBtn) {
          const id = parseInt(editBtn.dataset.id, 10);
          try {
            const tpl = await api.get(`/api/templates/${id}`);
            await _openEditor(id, tpl.name, tpl.htmlContent);
            // Row update handled by TemplateUpdated SignalR event.
          } catch (err) {
            toastr.error("Error: " + err.message);
          }
          return;
        }

        const browserBtn = e.target.closest(".btn-open-browser");
        if (browserBtn) {
          try {
            const tpl = await api.get(`/api/templates/${browserBtn.dataset.id}`);
            const w = window.open("", "_blank");
            if (w) { w.document.open(); w.document.write(tpl.htmlContent); w.document.close(); }
          } catch (err) {
            toastr.error("Error: " + err.message);
          }
          return;
        }

        const previewBtn = e.target.closest(".btn-preview");
        if (previewBtn) {
          await _showPreview(previewBtn.dataset.id, previewBtn.dataset.name);
          return;
        }

        const grabBtn = e.target.closest(".btn-grab");
        if (grabBtn) {
          await _doGrab(grabBtn.dataset.id, grabBtn.dataset.name, grabBtn);
          return;
        }

        const logBtn = e.target.closest(".btn-show-log");
        if (logBtn) {
          await _showGrabLog(logBtn.dataset.id, logBtn.dataset.name);
        }
      });
  }

  // ── Template editor (CodeMirror modal) ───────────────────────────────────

  /**
   * Opens the CodeMirror template editor modal for creating or editing a template.
   * @param {number|null} id          Template id for editing, or null for creating.
   * @param {string}      name        Initial name field value.
   * @param {string}      htmlContent Initial HTML content for the editor.
   * @returns {Promise<boolean>}      Resolves true if the template was saved, false if cancelled.
   */
  async function _openEditor(id, name, htmlContent) {
    const modal      = document.getElementById("tpl-editor-modal");
    const titleEl    = document.getElementById("tpl-editor-title");
    const nameInput  = document.getElementById("tpl-editor-name");
    const saveBtn    = document.getElementById("tpl-editor-save");
    const cmWrap     = document.getElementById("tpl-editor-cm-wrap");

    titleEl.textContent = id ? "Edit template" : "New template";
    nameInput.value     = name || "";

    const bsModal = bootstrap.Modal.getOrCreateInstance(modal);

    // Create the CodeMirror editor once; reuse on subsequent calls.
    if (!_cmEditor) {
      _cmEditor = CodeMirror(cmWrap, {
        mode          : "htmlmixed",
        theme         : "monokai",
        lineNumbers   : true,
        lineWrapping  : true,
        indentUnit    : 2,
        tabSize       : 2,
        indentWithTabs: false,
        extraKeys     : { "Ctrl-S": () => saveBtn.click() },
      });
      _cmEditor.setSize(null, "400px");
    }

    _cmEditor.setValue(
      htmlContent || "<!DOCTYPE html>\n<html>\n<body>\n\n</body>\n</html>"
    );

    // CodeMirror must repaint after the modal becomes visible.
    modal.addEventListener("shown.bs.modal", () => _cmEditor.refresh(), { once: true });

    return new Promise((resolve) => {
      let saved = false;

      async function onSave() {
        const n    = nameInput.value.trim();
        const html = _cmEditor.getValue();

        if (!n) {
          toastr.warning("Please enter a template name.");
          return;
        }

        const original = saveBtn.innerHTML;
        saveBtn.disabled = true;
        saveBtn.innerHTML = '<span class="spinner-border spinner-border-sm" role="status"></span>';

        try {
          if (id) {
            await api.put(`/api/templates/${id}`, { name: n, htmlContent: html });
            toastr.success("Template updated.");
          } else {
            await api.post("/api/templates", { name: n, htmlContent: html });
            toastr.success("Template created.");
          }
          saved = true;
          bsModal.hide();
        } catch (err) {
          toastr.error("Error: " + err.message);
        } finally {
          saveBtn.disabled = false;
          saveBtn.innerHTML = original;
        }
      }

      function onHidden() {
        saveBtn.removeEventListener("click", onSave);
        resolve(saved);
      }

      saveBtn.addEventListener("click", onSave);
      modal.addEventListener("hidden.bs.modal", onHidden, { once: true });

      bsModal.show();
      setTimeout(() => nameInput.focus(), 350);
    });
  }

  /** Opens the editor for a brand-new (unsaved) template. */
  async function _showCreateDialog() {
    await _openEditor(null, "", "<!DOCTYPE html>\n<html>\n<body>\n\n</body>\n</html>");
    // New row added by TemplateCreated SignalR event.
  }

  // ── Actions ───────────────────────────────────────────────────────────────

  async function _showPreview(id, name) {
    const wrap   = document.getElementById("preview-card-wrap");
    const iframe = document.getElementById("preview-iframe");
    document.getElementById("preview-title").textContent = `Preview — ${name}`;

    iframe.srcdoc = "<p style='padding:1rem;font-family:monospace;color:#6c757d;'>Loading…</p>";
    wrap?.classList.remove("d-none");
    wrap?.scrollIntoView({ behavior: "smooth", block: "start" });

    try {
      const tpl = await api.get(`/api/templates/${id}`);
      if (tpl?.htmlContent) iframe.srcdoc = tpl.htmlContent;
    } catch (err) {
      iframe.srcdoc = `<p style='color:red;padding:1rem;'>Failed to load: ${err.message}</p>`;
    }
  }

  /**
   * Posts an on-demand grab request then opens the log panel.
   * The result is delivered via SignalR (GrabCompleted); polling starts as a
   * fallback for when the SignalR connection is not available.
   */
  async function _doGrab(id, name, btn) {
    btn.disabled = true;
    const original = btn.innerHTML;
    btn.innerHTML = '<span class="spinner-border spinner-border-sm" role="status"></span>';

    try {
      await api.post(`/api/templates/${id}/grab`, {});
      toastr.info(`Grab queued for "${name}".`);
      await _showGrabLog(id, name, /* startPollingFallback= */ true);
    } catch (err) {
      toastr.error("Grab failed: " + err.message);
    } finally {
      btn.disabled = false;
      btn.innerHTML = original;
    }
  }

  async function _showGrabLog(id, name, startPollingFallback = false) {
    _stopPolling();
    _activeId = id;

    const wrap = document.getElementById("grab-card-wrap");
    wrap?.classList.remove("d-none");
    wrap?.scrollIntoView({ behavior: "smooth", block: "start" });

    await _refreshGrabLog(id);

    // Start the polling fallback; it stops itself when the grab completes or
    // when the SignalR GrabCompleted handler calls _stopPolling().
    if (startPollingFallback) {
      _startPolling(id);
    }
  }

  async function _refreshGrabLog(id) {
    let logs;
    try {
      logs = await api.get(`/api/templates/${id}/log`);
    } catch {
      return null;
    }

    const entries = logs || [];
    const badge   = document.getElementById("grab-status-badge");
    const tbody   = document.getElementById("grab-log-tbody");

    // ── Status badge ──────────────────────────────────────────────────────
    if (!entries.length) {
      if (badge) { badge.textContent = "No grabs yet"; badge.className = "badge bg-secondary"; }
    } else {
      const latest = entries[0];
      if (latest.success === null || latest.success === undefined) {
        if (badge) { badge.textContent = "Pending…"; badge.className = "badge bg-warning text-dark"; }
      } else if (latest.success) {
        if (badge) { badge.textContent = "Success"; badge.className = "badge bg-success"; }
      } else {
        if (badge) { badge.textContent = "Failed"; badge.className = "badge bg-danger"; }
      }
    }

    // ── Log table ─────────────────────────────────────────────────────────
    if (tbody) {
      tbody.innerHTML = entries.length
        ? entries.map(_logRow).join("")
        : `<tr><td colspan="5" class="text-center text-muted py-3">No grabs recorded.</td></tr>`;
    }

    // ── Preview image (last successful grab) ─────────────────────────────
    const previewWrap = document.getElementById("grab-preview-wrap");
    const img         = document.getElementById("grab-preview-img");
    const hasSuccess  = entries.some((e) => e.success === true);

    if (previewWrap && img) {
      if (hasSuccess) {
        img.src     = `/api/templates/${id}/preview-image?t=${Date.now()}`;
        img.onerror = () => previewWrap.classList.add("d-none");
        img.onload  = () => previewWrap.classList.remove("d-none");
      } else {
        previewWrap.classList.add("d-none");
      }
    }

    return entries[0] ?? null;
  }

  function _logRow(entry) {
    const statusBadge =
      entry.success === null || entry.success === undefined
        ? `<span class="badge bg-warning text-dark">Pending</span>`
        : entry.success
          ? `<span class="badge bg-success">OK</span>`
          : `<span class="badge bg-danger">Failed</span>`;

    return `
      <tr>
        <td class="small">${new Date(entry.requestedAt).toLocaleString()}</td>
        <td class="small">${entry.completedAt ? new Date(entry.completedAt).toLocaleString() : "—"}</td>
        <td>${statusBadge}</td>
        <td class="small text-muted">${_esc(entry.message || "")}</td>
        <td class="small text-muted">${entry.width}×${entry.height}</td>
      </tr>`;
  }

  // ── Polling (fallback when SignalR is unavailable) ────────────────────────

  function _startPolling(id) {
    _pollTimer = setInterval(async () => {
      if (_activeId !== id) { _stopPolling(); return; }

      const latest = await _refreshGrabLog(id);

      if (latest && latest.completedAt !== null && latest.completedAt !== undefined) {
        _stopPolling();
        if (latest.success) {
          toastr.success("Grab completed successfully!");
        } else {
          toastr.error("Grab failed: " + (latest.message || "Unknown error"));
        }
      }
    }, 5000);
  }

  function _stopPolling() {
    if (_pollTimer !== null) {
      clearInterval(_pollTimer);
      _pollTimer = null;
    }
  }

  // ── Utilities ─────────────────────────────────────────────────────────────

  function _esc(str) {
    return String(str)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  return { init };
})();
