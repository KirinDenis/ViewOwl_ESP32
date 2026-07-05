import { useState, useEffect, useRef } from 'react'
import { createPortal } from 'react-dom'
import 'esp-web-tools'
import { authFetch } from '../../utils/auth'
import SerialTerminalModal from '../Serial/SerialTerminalModal'
import './BurnModal.css'

const STEP_LABELS = ['CONFIG', 'FLASH', 'TOKEN']

// ── Display type catalogue ─────────────────────────────────────────────────
const DISPLAY_TYPES = [
  {
    id:       'ili9341',
    label:    '320 × 240',
    driver:   'ILI9341',
    board:    'CYD ESP32-2432S028',
    width:    320,
    height:   240,
    manifest: '/firmware/v1/manifest-320x240.json',
  },
  {
    id:       'st7796',
    label:    '480 × 320',
    driver:   'ST7796',
    board:    'CYD ESP32-3248S035',
    width:    480,
    height:   320,
    manifest: '/firmware/v1/manifest-480x320.json',
  },
  {
    id:       'ili9486',
    label:    '480 × 320',
    driver:   'ILI9486',
    board:    'OWLOSAirQuality RPi',
    width:    480,
    height:   320,
    manifest: '/firmware/v1/manifest-ili9486-480x320.json',
  },
  {
    id:       'gc9a01',
    label:    '240 × 240 round',
    driver:   'GC9A01',
    board:    'CrowPanel C3 1.28"',
    width:    240,
    height:   240,
    manifest: '/firmware/v1/manifest-gc9a01-240x240.json',
  },
  {
    id:       'epd',
    label:    '400 × 300 e-paper',
    driver:   'SSD1683',
    board:    '4.2" e-paper (ESP32-S3)',
    width:    400,
    height:   300,
    manifest: '/firmware/v1/manifest-epd-400x300.json',
  },
  {
    id:       'epdwide',
    label:    '792 × 272 wide e-paper',
    driver:   'SSD1683 ×2',
    board:    '5.79" wide e-paper (ESP32-S3)',
    width:    792,
    height:   272,
    manifest: '/firmware/v1/manifest-epd-792x272.json',
  },
]

// ── Pin profiles per display type ──────────────────────────────────────────
// BL = -1 means backlight is hardwired to power (no GPIO control).
// RST = -1 means software reset is used (no RST pin needed).
const PIN_PROFILES = {
  ili9341: [
    {
      id:   'cyd-2432s028',
      name: 'ESP32-2432S028 (CYD)',
      pins: { sclk: 14, mosi: 13, miso: 12, dc: 2, rst: -1, cs: 15, bl: 21 },
    },
    {
      id:   'cyd-2432s024',
      name: 'ESP32-2432S024 (CYD)',
      pins: { sclk: 14, mosi: 13, miso: 12, dc: 2, rst: -1, cs: 15, bl: 27 },
    },
    {
      id:   'vspi-generic',
      name: 'Generic ESP32 VSPI',
      pins: { sclk: 18, mosi: 23, miso: 19, dc: 2, rst:  4, cs: 15, bl: -1 },
    },
    { id: 'custom', name: 'Custom', pins: null },
  ],
  st7796: [
    {
      id:   'cyd-3248s035',
      name: 'ESP32-3248S035 (CYD)',
      pins: { sclk: 14, mosi: 13, miso: 12, dc:  2, rst: -1, cs: 15, bl: 27 },
    },
    {
      id:   'wt32-sc01',
      name: 'WT32-SC01',
      pins: { sclk: 14, mosi: 13, miso: -1, dc: 21, rst: 22, cs: 15, bl: 23 },
    },
    { id: 'custom', name: 'Custom', pins: null },
  ],
  ili9486: [
    {
      id:   'owlos-aq',
      name: 'OWLOSAirQuality',
      pins: { sclk: 18, mosi: 23, miso: 19, dc: 2, rst: 4, cs: 15, bl: -1 },
    },
    { id: 'custom', name: 'Custom', pins: null },
  ],
  // CrowPanel C3 1.28" is a fixed-pin board: RST + backlight are driven via the
  // PI4IOE5V6408 I2C expander and the round display is write-only, so the firmware
  // ignores any pin config sent over serial. One fixed profile, no custom option.
  gc9a01: [
    {
      id:   'crowpanel-c3',
      name: 'CrowPanel C3 1.28" (fixed)',
      pins: { sclk: 6, mosi: 7, miso: -1, dc: 2, rst: -1, cs: 10, bl: -1 },
    },
  ],
  // 4.2" e-paper (SSD1683) on ESP32-S3 is a fixed-pin board: the bit-bang SPI +
  // power pins are hardwired in firmware, so any pin config sent over serial is
  // ignored. One fixed profile, no custom option (like the CrowPanel C3).
  epd: [
    {
      id:   'epd-s3',
      name: '4.2" e-paper (fixed)',
      pins: { sclk: 12, mosi: 11, miso: -1, dc: 46, rst: 47, cs: 45, bl: -1 },
    },
  ],
  // 5.79" wide e-paper (two SSD1683 in master/slave cascade) on ESP32-S3 — same fixed
  // bit-bang wiring as the 4.2"; one CS drives both controllers. Pin config is ignored.
  epdwide: [
    {
      id:   'epdwide-s3',
      name: '5.79" wide e-paper (fixed)',
      pins: { sclk: 12, mosi: 11, miso: -1, dc: 46, rst: 47, cs: 45, bl: -1 },
    },
  ],
}

const PIN_LABELS = ['SCLK', 'MOSI', 'MISO', 'DC', 'RST', 'CS', 'BL']
const PIN_KEYS   = ['sclk', 'mosi', 'miso', 'dc', 'rst', 'cs', 'bl']

// ── Helpers ────────────────────────────────────────────────────────────────

/** Generates a RFC-4122 v4 UUID string. */
function generateUUID() {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
    const r = (Math.random() * 16) | 0
    return (c === 'x' ? r : (r & 0x3) | 0x8).toString(16)
  })
}

/**
 * Converts a 32-char hex token back to RFC-4122 UUID format.
 */
function hexToUUID(hex) {
  const h = hex.replace(/-/g, '')
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20, 32)}`
}

/** Returns the first profile for the given display id. */
function defaultProfile(displayId) {
  return PIN_PROFILES[displayId][0]
}

// ── Component ──────────────────────────────────────────────────────────────

/**
 * @param {object}   props
 * @param {function} props.onClose
 * @param {function} [props.onDone]
 * @param {string}   [props.prefillToken]
 * @param {string}   [props.prefillName]
 * @param {boolean}  [props.isReflash]
 */
function BurnModal({ onClose, onDone, prefillToken = null, prefillName = null, isReflash = false }) {
  const [step,             setStep]           = useState(1)
  const [displayId,        setDisplayId]      = useState(DISPLAY_TYPES[0].id)
  const [token,            setToken]          = useState(() => prefillToken ? hexToUUID(prefillToken) : generateUUID())
  const [tokenCopied,      setTokenCopied]    = useState(false)
  const [createdDeviceId,  setCreatedDeviceId] = useState(null)

  // Pin profile state
  const [pinProfileId, setPinProfileId] = useState(() => defaultProfile(DISPLAY_TYPES[0].id).id)
  const [pinValues,    setPinValues]    = useState(() => ({ ...defaultProfile(DISPLAY_TYPES[0].id).pins }))

  // Step 3 provision state
  const [wifiSsid,    setWifiSsid]    = useState('')
  const [wifiPass,    setWifiPass]    = useState('')
  const [provState,   setProvState]   = useState('idle')   // idle | done (set by the Serial Terminal)
  const [termOpen,    setTermOpen]    = useState(false)
  const [regState,    setRegState]    = useState('idle')
  const [deviceName,  setDeviceName]  = useState(prefillName ?? '')

  const selectedDisplay = DISPLAY_TYPES.find(d => d.id === displayId)
  const webSerialOk     = 'serial' in navigator

  // Reset pin profile when display type changes
  useEffect(() => {
    const first = defaultProfile(displayId)
    setPinProfileId(first.id)
    setPinValues({ ...first.pins })
  }, [displayId])

  // Close on Escape
  useEffect(() => {
    const onKey = e => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  const installBtnRef = useRef(null)
  useEffect(() => {
    const el = installBtnRef.current
    if (!el) return
    const handler = e => { if (e.detail?.state === 'finished') setStep(3) }
    el.addEventListener('state-changed', handler)
    return () => el.removeEventListener('state-changed', handler)
  }, [step])

  // Pin profile helpers
  const handleProfileSelect = (profileId) => {
    const profile = PIN_PROFILES[displayId].find(p => p.id === profileId)
    setPinProfileId(profileId)
    if (profile.pins) setPinValues({ ...profile.pins })
  }

  const handlePinChange = (key, raw) => {
    const val = raw === '' ? '' : parseInt(raw, 10)
    setPinProfileId('custom')
    setPinValues(prev => ({ ...prev, [key]: isNaN(val) ? prev[key] : val }))
  }

  // Token helpers
  const handleTokenCopy  = () => {
    navigator.clipboard.writeText(token).catch(() => {})
    setTokenCopied(true)
    setTimeout(() => setTokenCopied(false), 2000)
  }
  const handleTokenRegen = () => {
    if (isReflash) return
    setToken(generateUUID())
    setTokenCopied(false)
    setRegState('idle')
  }

  const handleProceedToFlash = async () => {
    if (isReflash) { setStep(2); return }
    setRegState('pending')
    try {
      const tokenHex = token.replace(/-/g, '')
      const res = await authFetch('/api/devices', {
        method:  'POST',
        headers: { 'Content-Type': 'application/json' },
        body:    JSON.stringify({
          name:          deviceName.trim(),
          token:         tokenHex,
          displayWidth:  selectedDisplay.width,
          displayHeight: selectedDisplay.height,
          displayType:   selectedDisplay.driver,
        }),
      })
      if (res?.ok || res?.status === 409) {
        setRegState('done')
        if (res?.ok) {
          const device = await res.json()
          setCreatedDeviceId(device.id)
        }
        setStep(2)
      } else {
        setRegState('error')
      }
    } catch {
      setRegState('error')
    }
  }

  // Provisioning is handled by the Serial Terminal popup (robust continuous reader +
  // live device console). It sends token / Wi-Fi / pins and reports VIEWOWL_PROV_DONE.
  const openTerminal = () => setTermOpen(true)

  const isStepDone   = (n) => step > n || (n === 3 && provState === 'done')
  const isStepActive = (n) => step === n && !isStepDone(n)

  const handleReturnToDashboard = () => {
    if (isReflash) { onClose(); return }
    if (onDone) onDone(createdDeviceId)
    onClose()
  }

  const handleClose = () => {
    if (createdDeviceId) handleReturnToDashboard()
    else onClose()
  }

  // ── Render ────────────────────────────────────────────────────────────
  return (
    <>
    <div className="burn-overlay" onClick={e => e.target === e.currentTarget && handleClose()}>
      <div className="burn-modal">

        {/* Header */}
        <div className="burn-header">
          <div className="burn-header-left">
            <span className="burn-header-icon">{isReflash ? '↺' : '⚡'}</span>
            <span className="burn-header-label">{isReflash ? 'REBURN — ' + prefillName : 'FLASH ESP32 FIRMWARE'}</span>
          </div>
          <div className="burn-header-steps">
            {[1, 2, 3].map(n => (
              <span key={n} className={`burn-step-dot${isStepActive(n) ? ' active' : isStepDone(n) ? ' done' : ''}`}>
                <span className="burn-step-num">{isStepDone(n) ? '✓' : n}</span>
                <span className="burn-step-lbl">{STEP_LABELS[n - 1]}</span>
              </span>
            ))}
          </div>
          <button className="burn-close-btn" onClick={handleClose}>✕</button>
        </div>

        {/* Body */}
        <div className="burn-body">

          {/* ── STEP 1: Configure ───────────────────────────────── */}
          {step === 1 && (
            <>
              <div className="burn-section">
                <div className="burn-section-title">DISPLAY CONTROLLER</div>
                <div className="burn-display-grid">
                  {DISPLAY_TYPES.map(d => (
                    <button
                      key={d.id}
                      className={`burn-display-btn${displayId === d.id ? ' selected' : ''}`}
                      onClick={() => setDisplayId(d.id)}
                    >
                      <span className="burn-display-res">{d.label}</span>
                      <span className="burn-display-drv">{d.driver}</span>
                      <span className="burn-display-board">{d.board}</span>
                    </button>
                  ))}
                </div>
              </div>

              <div className="burn-section">
                <div className="burn-section-title">DEVICE NAME</div>
                {!isReflash && (
                  <div className="burn-section-hint">
                    A unique name for this controller on your dashboard.
                  </div>
                )}
                <div className="burn-field-row">
                  <span className="burn-field-label">NAME</span>
                  <input
                    className={`burn-field-input${isReflash ? ' burn-field-locked' : ''}`}
                    value={deviceName}
                    onChange={e => { if (!isReflash) setDeviceName(e.target.value) }}
                    placeholder="e.g. Living Room Display"
                    spellCheck={false}
                    readOnly={isReflash}
                    autoFocus={!isReflash}
                  />
                  {isReflash && <span className="burn-locked-badge">LOCKED</span>}
                </div>
              </div>

              <div className="burn-section">
                <div className="burn-section-title">DEVICE TOKEN</div>
                {!isReflash && (
                  <div className="burn-section-hint">
                    This token links the device to your dashboard.
                    It will be sent over USB after flashing.
                  </div>
                )}
                {isReflash && (
                  <div className="burn-section-hint burn-section-hint-reburn">
                    Existing token — will be re-sent to the device after flashing.
                  </div>
                )}
                <div className="burn-field-row">
                  <span className="burn-field-label">TOKEN</span>
                  <div className="burn-field-input burn-field-input-ro">{token}</div>
                  <button
                    className={`burn-icon-btn${tokenCopied ? ' burn-icon-btn-ok' : ''}`}
                    onClick={handleTokenCopy}
                    title="Copy token"
                  >{tokenCopied ? '✓' : '⎘'}</button>
                  {!isReflash && (
                    <button
                      className="burn-icon-btn burn-icon-btn-warn"
                      onClick={handleTokenRegen}
                      title="Generate new token"
                    >↻</button>
                  )}
                </div>
              </div>

              {!webSerialOk && (
                <div className="burn-no-serial">
                  <strong>Web Serial API is not available.</strong><br />
                  Please open this page in <strong>Google Chrome</strong> or <strong>Microsoft Edge</strong> (desktop) over HTTPS.
                </div>
              )}

              <div className="burn-section">
                <div className="burn-section-title">REQUIREMENTS</div>
                <div className="burn-reqs">
                  <div className={`burn-req ${webSerialOk ? 'burn-req-ok' : 'burn-req-err'}`}>
                    {webSerialOk ? '✓' : '✗'}&nbsp;
                    {webSerialOk
                      ? 'Web Serial API available (Chrome / Edge)'
                      : 'Web Serial not supported — use Chrome or Edge over HTTPS'}
                  </div>
                  <div className="burn-req burn-req-ok">✓ ESP32 connected via USB</div>
                  <div className="burn-req burn-req-ok">✓ HTTPS active — required for Web Serial</div>
                </div>
              </div>

              {regState === 'error' && (
                <div className="burn-reg-error">
                  Registration failed — server did not accept the device.
                  Check your connection and try again, or regenerate the token.
                </div>
              )}

              <button
                className="burn-flash-btn"
                disabled={!webSerialOk || regState === 'pending' || (!isReflash && !deviceName.trim())}
                onClick={handleProceedToFlash}
              >
                {regState === 'pending' ? 'REGISTERING…'
                  : isReflash ? '↺ STEP 2 — REBURN DEVICE →'
                  : 'STEP 2 — FLASH DEVICE →'}
              </button>
            </>
          )}

          {/* ── STEP 2: Flash ────────────────────────────────────── */}
          {step === 2 && (
            <>
              <div className="burn-fw-strip">
                <span className="burn-fw-chip">ESP32</span>
                <span className="burn-fw-sep">│</span>
                <span className="burn-fw-res">{selectedDisplay.label}</span>
                <span className="burn-fw-sep">│</span>
                <span className="burn-fw-name">{selectedDisplay.driver}</span>
                <span className="burn-fw-sep">│</span>
                <span className="burn-fw-name">{selectedDisplay.board}</span>
              </div>

              <div className="burn-section">
                <div className="burn-section-title">FLASH FIRMWARE</div>
                <div className="burn-section-hint">
                  Click the button to connect and flash the firmware.
                  Wi-Fi credentials, token and pin configuration will be sent in the next step.
                </div>
              </div>

              <div className="burn-flash-section">
                <esp-web-install-button
                  ref={installBtnRef}
                  manifest={selectedDisplay.manifest}
                >
                  <button slot="activate" className="burn-flash-btn">
                    ⚡&nbsp;&nbsp;CONNECT &amp; FLASH
                  </button>
                </esp-web-install-button>
              </div>

              <button className="burn-flash-btn burn-flash-btn-alt" onClick={() => setStep(3)}>
                ALREADY FLASHED — SKIP TO TOKEN
              </button>
            </>
          )}

          {/* ── STEP 3: Provision ───────────────────────────────── */}
          {step === 3 && (
            <>
              <div className="burn-section">
                <div className="burn-section-title">SEND CREDENTIALS TO DEVICE</div>
                <div className="burn-section-hint">
                  The device is waiting over USB Serial (up to 2 min after boot).
                  Select your board pinout, enter Wi-Fi credentials, then click Connect.
                </div>
              </div>

              <div className="burn-field-row" style={{ marginBottom: '4px' }}>
                <span className="burn-field-label">TOKEN</span>
                <div className="burn-field-input burn-field-input-ro">{token}</div>
                <button
                  className={`burn-icon-btn${tokenCopied ? ' burn-icon-btn-ok' : ''}`}
                  onClick={handleTokenCopy}
                  title="Copy token"
                >{tokenCopied ? '✓' : '⎘'}</button>
              </div>

              {provState === 'idle' && (
                <>
                  {/* WiFi */}
                  <div className="burn-field-row">
                    <span className="burn-field-label">SSID</span>
                    <input
                      className="burn-field-input"
                      type="text"
                      placeholder="Network name"
                      value={wifiSsid}
                      onChange={e => setWifiSsid(e.target.value)}
                      autoComplete="off"
                      spellCheck={false}
                      maxLength={32}
                    />
                  </div>
                  <div className="burn-field-row">
                    <span className="burn-field-label">PASS</span>
                    <input
                      className="burn-field-input"
                      type="password"
                      placeholder="leave empty for open network"
                      value={wifiPass}
                      onChange={e => setWifiPass(e.target.value)}
                      autoComplete="new-password"
                      maxLength={64}
                    />
                  </div>

                  {/* Pin profiles */}
                  <div className="burn-section">
                    <div className="burn-section-title">PIN CONFIGURATION</div>
                    <div className="burn-section-hint">
                      Select the wiring preset that matches your board, or edit pins manually.
                    </div>
                    <div className="burn-profile-row">
                      {PIN_PROFILES[displayId].map(p => (
                        <button
                          key={p.id}
                          className={`burn-profile-btn${pinProfileId === p.id ? ' selected' : ''}`}
                          onClick={() => handleProfileSelect(p.id)}
                        >
                          {p.name}
                        </button>
                      ))}
                    </div>
                    <div className="burn-pin-grid">
                      {PIN_KEYS.map((key, i) => (
                        <div key={key} className="burn-pin-cell">
                          <span className="burn-pin-label">{PIN_LABELS[i]}</span>
                          <input
                            className="burn-pin-input"
                            type="number"
                            min="-1"
                            max="39"
                            value={pinValues[key] ?? -1}
                            onChange={e => handlePinChange(key, e.target.value)}
                          />
                        </div>
                      ))}
                    </div>
                    <div className="burn-pin-hint">
                      −1 = not connected / hardwired
                    </div>
                  </div>

                  <div className="burn-flash-section">
                    <button
                      className="burn-flash-btn"
                      onClick={openTerminal}
                      disabled={wifiSsid.trim().length === 0}
                      title={wifiSsid.trim().length === 0 ? 'Enter Wi-Fi SSID first' : undefined}
                    >
                      ⚡&nbsp;&nbsp;OPEN SERIAL TERMINAL &amp; PROVISION
                    </button>
                  </div>
                </>
              )}

              {provState === 'done' && (
                <>
                  <div className="burn-prov-status burn-prov-ok">
                    ✓ Done — device is rebooting and will connect automatically.
                  </div>
                  <button className="burn-flash-btn burn-done-btn" onClick={handleReturnToDashboard}>
                    ← RETURN TO DASHBOARD
                  </button>
                </>
              )}
            </>
          )}

        </div>
      </div>
    </div>

    {termOpen && createPortal(
      <SerialTerminalModal
        prefillToken={token}
        prefillSsid={wifiSsid}
        prefillPass={wifiPass}
        pins={pinValues}
        deviceName={deviceName}
        onClose={() => setTermOpen(false)}
        onProvisioned={() => { setProvState('done'); setTermOpen(false) }}
      />,
      document.body
    )}
    </>
  )
}

export default BurnModal
