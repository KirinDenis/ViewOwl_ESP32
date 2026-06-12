// Module-level slot — only one dialog can be open at a time.
let _openFn = null

/**
 * Called by ConfirmModal on mount to register itself as the dialog handler.
 * Passing null deregisters (cleanup on unmount).
 *
 * @param {((message: string, opts: object) => Promise<boolean>) | null} fn
 */
export function _registerConfirmModal(fn) {
    _openFn = fn
}

/**
 * Shows a custom confirmation dialog.
 * Falls back to the native window.confirm when the modal component is not mounted.
 *
 * @param {string} message  Body text shown to the user.
 * @param {{ title?: string, danger?: boolean, yes?: string, no?: string }} [opts]
 * @returns {Promise<boolean>}  Resolves true when user clicks YES, false otherwise.
 */
export function confirm(message, opts = {}) {
    if (!_openFn) return Promise.resolve(window.confirm(message))
    return _openFn(message, opts)
}
