/**
 * @file pages/users.js
 * @description Admin-only users management page.
 * Only rendered when the authenticated user's role === "Admin".
 */

const usersPage = (() => {
  async function init() {
    const container = document.getElementById("page-content");

    let users;
    try {
      users = await api.get("/api/admin/users");
    } catch (err) {
      container.innerHTML = `<div class="alert alert-danger m-3">${err.message}</div>`;
      return;
    }

    container.innerHTML = _skeleton();
    _render(users || []);
    _bindEvents();
  }

  // ── HTML builders ─────────────────────────────────────────────────────────

  function _skeleton() {
    return `
      <div class="container-fluid p-0 page-anim">
        <div class="d-flex justify-content-between align-items-center mb-3">
          <h1 class="h3 mb-0">Users</h1>
          <button class="btn btn-primary btn-sm" id="btn-add-user">
            <i class="fas fa-plus me-1"></i> Add user
          </button>
        </div>
        <div class="row">
          <div class="col-12">
            <div class="card">
              <div class="card-header">
                <h5 class="card-title mb-0">All users</h5>
              </div>
              <div class="card-body p-0">
                <div class="table-responsive">
                  <table class="table table-hover mb-0">
                    <thead class="table-light">
                      <tr>
                        <th>Login</th>
                        <th>Role</th>
                        <th>Sandbox</th>
                        <th>Created</th>
                        <th></th>
                      </tr>
                    </thead>
                    <tbody id="users-tbody"></tbody>
                  </table>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>`;
  }

  function _render(users) {
    const tbody = document.getElementById("users-tbody");
    if (!users.length) {
      tbody.innerHTML = `
        <tr>
          <td colspan="5" class="text-center text-muted py-4">No users found.</td>
        </tr>`;
      return;
    }
    tbody.innerHTML = users.map(_row).join("");
  }

  function _row(u) {
    const roleBadge = u.role === "Admin"
      ? `<span class="badge bg-danger">Admin</span>`
      : `<span class="badge bg-info text-dark">User</span>`;

    // Bootstrap 5 form-switch (replaces BS4 custom-control custom-switch)
    const sandboxToggle = `
      <div class="form-check form-switch d-inline-block mb-0">
        <input type="checkbox" class="form-check-input sandbox-toggle"
               id="sb-${u.id}" data-id="${u.id}" role="switch"
               ${u.sandboxEnabled ? "checked" : ""}>
        <label class="form-check-label" for="sb-${u.id}"></label>
      </div>`;

    return `
      <tr data-id="${u.id}">
        <td class="align-middle fw-semibold">${_esc(u.login)}</td>
        <td class="align-middle">${roleBadge}</td>
        <td class="align-middle">${sandboxToggle}</td>
        <td class="align-middle text-muted small">${new Date(u.createdAt).toLocaleDateString()}</td>
        <td class="align-middle text-end">
          <button class="btn btn-sm btn-outline-danger btn-delete-user" data-id="${u.id}">
            <i class="fas fa-trash"></i>
          </button>
        </td>
      </tr>`;
  }

  // ── Event bindings ────────────────────────────────────────────────────────

  function _bindEvents() {
    document.getElementById("btn-add-user")?.addEventListener("click", _showCreateDialog);

    document.getElementById("users-tbody")?.addEventListener("click", async (e) => {
      const btn = e.target.closest(".btn-delete-user");
      if (!btn) return;
      const confirmed = await dialog.confirm(
        "Delete this user and all their devices and templates?",
        "Delete"
      );
      if (!confirmed) return;
      try {
        await api.delete(`/api/admin/users/${btn.dataset.id}`);
        toastr.success("User deleted.");
        await init();
      } catch (err) {
        toastr.error("Error: " + err.message);
      }
    });

    document.getElementById("users-tbody")?.addEventListener("change", async (e) => {
      const toggle = e.target.closest(".sandbox-toggle");
      if (!toggle) return;
      try {
        await api.patch(`/api/admin/users/${toggle.dataset.id}/sandbox`, {
          enabled: toggle.checked,
        });
        toastr.success(`Sandbox ${toggle.checked ? "enabled" : "disabled"}.`);
      } catch (err) {
        toastr.error("Error: " + err.message);
        toggle.checked = !toggle.checked; // revert on failure
      }
    });
  }

  // ── Create dialog ─────────────────────────────────────────────────────────

  async function _showCreateDialog() {
    const login = await dialog.prompt("New user login", "e.g. john");
    if (!login?.trim()) return;

    const password = await dialog.prompt("Password", "at least 8 characters");
    if (!password) return;

    const roleRaw = await dialog.prompt("Role", "", "User");
    const role    = ["Admin", "User"].includes(roleRaw) ? roleRaw : "User";

    try {
      await api.post("/api/admin/users", {
        login:         login.trim(),
        password,
        role,
        sandboxEnabled: true
      });
      toastr.success("User created.");
      await init();
    } catch (err) {
      toastr.error("Error: " + err.message);
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
