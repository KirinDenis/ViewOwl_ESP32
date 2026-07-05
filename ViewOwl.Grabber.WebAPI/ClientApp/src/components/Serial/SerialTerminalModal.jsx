import { useState, useEffect, useRef, useCallback } from 'react'
import './SerialTerminalModal.css'

// ── Token helpers ───────────────────────────────────────────────────────────
// The firmware stores the 32-char token verbatim and replays its bytes in HELLO;
// the server reads those bytes as a Windows GUID (Data1/2/3 little-endian). So the
// value sent over serial must be the byte-shuffled form — identical to BurnModal's
// uuidToGuidHex(). Accepts a UUID (with dashes) or a plain 32-char hex string.
function toGuidHex(token) {
  const h = (token || '').replace(/-/g, '').toLowerCase()
  if (h.length !== 32) return h   // let the firmware answer bad_length
  return h.slice(6, 8) + h.slice(4, 6) + h.slice(2, 4) + h.slice(0, 2)
       + h.slice(10, 12) + h.slice(8, 10)
       + h.slice(14, 16) + h.slice(12, 14)
       + h.slice(16, 32)
}

/** Normalises a UUID-or-hex token to dashed UUID form for display. */
function asUuid(token) {
  const h = (token || '').replace(/-/g, '').toLowerCase()
  if (h.length !== 32) return token || ''
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20, 32)}`
}

const sleep = (ms) => new Promise(r => setTimeout(r, ms))

/** Generates an RFC-4122 v4 UUID (display/copy form; sent byte-shuffled via toGuidHex). */
function generateUUID() {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
    const r = (Math.random() * 16) | 0
    return (c === 'x' ? r : (r & 0x3) | 0x8).toString(16)
  })
}

/**
 * A self-contained Web Serial terminal + provisioning helper. Owns its own port
 * connection (continuous reader — no readUntil buffer-discard races), shows the
 * live device log, and sends VIEWOWL_* lines. Replaces the esp-web-tools serial
 * provisioning step.
 *
 * @param {object}   props
 * @param {string}   [props.prefillToken]   UUID or 32-char hex; sent (byte-shuffled) on Provision.
 * @param {string}   [props.prefillSsid]
 * @param {string}   [props.prefillPass]
 * @param {object}   [props.pins]           { sclk, mosi, miso, dc, rst, cs, bl } — sent as VIEWOWL_PIN_* before creds.
 * @param {string}   [props.deviceName]
 * @param {function} props.onClose
 * @param {function} [props.onProvisioned]  Called once VIEWOWL_PROV_DONE is seen.
 */
function SerialTerminalModal({
  prefillToken = '', prefillSsid = '', prefillPass = '',
  pins = null, deviceName = '', onClose, onProvisioned,
}) {
  const [status,   setStatus]   = useState('disconnected')  // disconnected | connected | error
  const [lines,    setLines]    = useState([])              // { cls, text }
  const [token,    setToken]    = useState(asUuid(prefillToken))
  const [ssid,     setSsid]     = useState(prefillSsid)
  const [pass,     setPass]     = useState(prefillPass)
  const [showPass, setShowPass] = useState(false)
  const [raw,      setRaw]      = useState('')
  const [busy,     setBusy]     = useState(false)           // a provision sequence is in flight

  const portRef    = useRef(null)
  const readerRef  = useRef(null)
  const writerRef  = useRef(null)
  const readingRef = useRef(false)
  const doneRef    = useRef(false)   // onProvisioned fired
  const logRef     = useRef(null)

  const webSerialOk = typeof navigator !== 'undefined' && 'serial' in navigator
  const connected   = status === 'connected'

  const log = useCallback((text, cls = 'rx') => {
    setLines(prev => {
      const next = prev.length > 4000 ? prev.slice(-3000) : prev.slice()
      next.push({ text, cls })
      return next
    })
  }, [])

  // Auto-scroll to bottom on new output
  useEffect(() => {
    const el = logRef.current
    if (el) el.scrollTop = el.scrollHeight
  }, [lines])

  const disconnect = useCallback(async () => {
    readingRef.current = false
    try { if (readerRef.current) { await readerRef.current.cancel(); readerRef.current.releaseLock() } } catch { /* ignore */ }
    try { if (writerRef.current) { writerRef.current.releaseLock() } } catch { /* ignore */ }
    try { if (portRef.current)   { await portRef.current.close() } }   catch { /* ignore */ }
    readerRef.current = writerRef.current = portRef.current = null
    setStatus('disconnected')
  }, [])

  // Esc closes; disconnect on unmount
  useEffect(() => {
    const onKey = (e) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => {
      window.removeEventListener('keydown', onKey)
      readingRef.current = false
      // best-effort release without awaiting (component is going away)
      try { readerRef.current?.cancel() } catch { /* ignore */ }
      try { portRef.current?.close() }    catch { /* ignore */ }
    }
  }, [onClose])

  const readLoop = useCallback(async () => {
    const td = new TextDecoder()
    let acc = ''
    while (readingRef.current && portRef.current?.readable) {
      const reader = portRef.current.readable.getReader()
      readerRef.current = reader
      try {
        for (;;) {
          const { value, done } = await reader.read()
          if (done) break
          if (!value) continue
          const text = td.decode(value, { stream: true })
          log(text, 'rx')
          acc = (acc + text).slice(-256)
          if (!doneRef.current && acc.includes('VIEWOWL_PROV_DONE')) {
            doneRef.current = true
            log('--- provisioning complete — device will reboot ---', 'sys')
            onProvisioned?.()
          }
        }
      } catch (e) {
        if (readingRef.current) log(`read error: ${e.message}\n`, 'erl')
      } finally {
        try { reader.releaseLock() } catch { /* ignore */ }
      }
    }
  }, [log, onProvisioned])

  const connect = useCallback(async () => {
    if (!webSerialOk) return
    try {
      const port = await navigator.serial.requestPort()
      await port.open({ baudRate: 115200 })
      portRef.current   = port
      writerRef.current = port.writable.getWriter()
      readingRef.current = true
      doneRef.current    = false
      setStatus('connected')
      log('--- connected @115200 ---\n', 'sys')
      readLoop()
    } catch (e) {
      setStatus('error')
      log(`connect failed: ${e.message}\n`, 'erl')
    }
  }, [webSerialOk, log, readLoop])

  const sendLine = useCallback(async (line) => {
    if (!writerRef.current) { log('not connected\n', 'erl'); return false }
    try {
      await writerRef.current.write(new TextEncoder().encode(line + '\n'))
      log(`» ${line}\n`, 'tx')
      return true
    } catch (e) {
      log(`write error: ${e.message}\n`, 'erl')
      return false
    }
  }, [log])

  const sendRaw = async () => {
    const v = raw.trim()
    if (!v) return
    await sendLine(v)
    setRaw('')
  }

  const provision = async () => {
    if (busy) return
    if (!connected) { log('connect first\n', 'erl'); return }
    if (!token.replace(/-/g, '').match(/^[0-9a-fA-F]{32}$/)) { log('token must be 32 hex chars\n', 'erl'); return }
    if (!ssid.trim()) { log('enter SSID\n', 'erl'); return }
    setBusy(true)
    doneRef.current = false
    try {
      if (pins) {
        for (const key of ['sclk', 'mosi', 'miso', 'dc', 'rst', 'cs', 'bl']) {
          if (pins[key] != null) { await sendLine(`VIEWOWL_PIN_${key.toUpperCase()}:${pins[key]}`); await sleep(60) }
        }
      }
      await sendLine(`VIEWOWL_TOKEN:${toGuidHex(token)}`)
      await sleep(300)
      await sendLine(`VIEWOWL_WIFI_SSID:${ssid.trim()}`)
      await sleep(300)
      await sendLine(`VIEWOWL_WIFI_PASS:${pass}`)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="st-overlay" onClick={e => { if (e.target === e.currentTarget) onClose() }}>
      <div className="st-modal">

        <div className="st-header">
          <span className="st-title">🦉 SERIAL TERMINAL</span>
          {deviceName && <span className="st-device">{deviceName}</span>}
          <span className={`st-status st-status-${status}`}>
            {status === 'connected' ? '● connected @115200' : status === 'error' ? '● error' : '○ disconnected'}
          </span>
          <span className="st-grow" />
          <button className="st-btn" onClick={() => setLines([])}>Clear</button>
          <button
            className={`st-btn st-btn-primary${connected ? ' st-btn-on' : ''}`}
            onClick={() => connected ? disconnect() : connect()}
            disabled={!webSerialOk}
          >
            {connected ? 'Disconnect' : 'Connect'}
          </button>
          <button className="st-close" onClick={onClose} title="Close (Esc)">✕</button>
        </div>

        {!webSerialOk && (
          <div className="st-nowserial">
            Web Serial is unavailable — open the dashboard in <strong>Chrome</strong> or <strong>Edge</strong> over HTTPS.
          </div>
        )}

        <div className="st-log" ref={logRef}>
          {lines.length === 0
            ? <span className="st-hint-line">Click Connect, pick the device COM port, wait for VIEWOWL_READY, then Provision.</span>
            : lines.map((l, i) => <span key={i} className={`st-${l.cls}`}>{l.text}</span>)}
        </div>

        <div className="st-controls">
          {/* Raw line */}
          <div className="st-row">
            <label className="st-label">Send</label>
            <input
              className="st-input st-grow"
              value={raw}
              onChange={e => setRaw(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter') sendRaw() }}
              placeholder="raw line, Enter to send (e.g. VIEWOWL_WIFI_SSID:test)"
              autoComplete="off" spellCheck={false}
              disabled={!connected}
            />
            <button className="st-btn" onClick={sendRaw} disabled={!connected}>Send</button>
          </div>

          {/* Provisioning */}
          <div className="st-row">
            <label className="st-label">Token</label>
            <input
              className="st-input st-grow st-mono"
              value={token}
              onChange={e => setToken(e.target.value)}
              placeholder="device token (UUID or 32-hex)"
              autoComplete="off" spellCheck={false}
            />
          </div>
          <div className="st-row">
            <label className="st-label">SSID</label>
            <input
              className="st-input st-grow"
              value={ssid}
              onChange={e => setSsid(e.target.value)}
              placeholder="Wi-Fi SSID (2.4 GHz only)"
              autoComplete="off" spellCheck={false} maxLength={32}
            />
            <label className="st-label">Pass</label>
            <input
              className="st-input st-grow"
              type={showPass ? 'text' : 'password'}
              value={pass}
              onChange={e => setPass(e.target.value)}
              placeholder="Wi-Fi password"
              autoComplete="new-password" maxLength={64}
            />
            <button
              className="st-btn st-eye"
              onClick={() => setShowPass(s => !s)}
              title={showPass ? 'Hide password' : 'Show password'}
            >{showPass ? '🙈' : '👁'}</button>
          </div>
          <div className="st-row">
            {pins && <span className="st-pins">pins: {['sclk','mosi','miso','dc','rst','cs','bl'].map(k => `${k.toUpperCase()}${pins[k]}`).join(' ')}</span>}
            <span className="st-grow" />
            <button
              className="st-btn st-btn-go"
              onClick={provision}
              disabled={!connected || busy || !ssid.trim()}
              title={!connected ? 'Connect first' : !ssid.trim() ? 'Enter SSID' : 'Send token + Wi-Fi to the device'}
            >
              {busy ? 'SENDING…' : '⚡ PROVISION (token → ssid → pass)'}
            </button>
          </div>
          <div className="st-tip">
            Wait for <b>VIEWOWL_READY</b> before Provision. The device replies <b>…_OK</b> per line and
            <b> VIEWOWL_PROV_DONE</b> when stored, then reboots. ESP32 Wi-Fi is <b>2.4 GHz only</b>.
          </div>
        </div>

      </div>
    </div>
  )
}

export default SerialTerminalModal
