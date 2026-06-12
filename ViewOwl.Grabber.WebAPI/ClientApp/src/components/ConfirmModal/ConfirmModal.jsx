import { useState, useEffect, useCallback } from 'react'
import { createPortal } from 'react-dom'
import { _registerConfirmModal } from '../../utils/confirm'
import './ConfirmModal.css'

/**
 * Global confirmation dialog — rendered once in App.jsx.
 * Controlled via the confirm() utility in src/utils/confirm.js.
 */
export default function ConfirmModal() {
    // dialog = { message, opts, resolve } | null
    const [dialog, setDialog] = useState(null)

    // Register as the active handler on mount; deregister on unmount.
    useEffect(() => {
        _registerConfirmModal((message, opts) =>
            new Promise(resolve => setDialog({ message, opts, resolve }))
        )
        return () => _registerConfirmModal(null)
    }, [])

    const answer = useCallback((yes) => {
        setDialog(prev => {
            if (prev) prev.resolve(yes)
            return null
        })
    }, [])

    // Keyboard: Enter → confirm, Escape → cancel.
    // Capture phase + stopPropagation so the Escape never reaches underlying modals.
    useEffect(() => {
        if (!dialog) return
        const onKey = (e) => {
            if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); answer(false) }
            if (e.key === 'Enter')  { e.preventDefault(); e.stopPropagation(); answer(true)  }
        }
        window.addEventListener('keydown', onKey, { capture: true })
        return () => window.removeEventListener('keydown', onKey, { capture: true })
    }, [dialog, answer])

    if (!dialog) return null

    const { message, opts = {} } = dialog
    const title   = opts.title   ?? 'CONFIRM'
    const danger  = opts.danger  ?? false
    const yesText = opts.yes     ?? 'YES'
    const noText  = opts.no      ?? 'NO'

    return createPortal(
        <div className="cdlg-overlay" onClick={() => answer(false)}>
            <div className="cdlg-box" onClick={e => e.stopPropagation()}>
                <div className="cdlg-title">{title}</div>
                <div className="cdlg-message">{message}</div>
                <div className="cdlg-actions">
                    <button className="cdlg-btn cdlg-btn-no" onClick={() => answer(false)}>
                        {noText}
                    </button>
                    <button
                        className={`cdlg-btn cdlg-btn-yes${danger ? ' cdlg-btn-danger' : ''}`}
                        onClick={() => answer(true)}
                        autoFocus
                    >
                        {yesText}
                    </button>
                </div>
            </div>
        </div>,
        document.body
    )
}
