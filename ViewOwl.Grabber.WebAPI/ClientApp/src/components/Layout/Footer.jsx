import { useState, useEffect } from 'react'
import StatusLED from '../HUD/StatusLED'
import './Footer.css'

function Footer({ connectionState }) {
  const [ts, setTs] = useState(new Date().toISOString())
  useEffect(() => {
    const t = setInterval(() => setTs(new Date().toISOString()), 1000)
    return () => clearInterval(t)
  }, [])

  return (
    <footer className="app-footer hud-panel">
      <div className="footer-left">
        <StatusLED status={connectionState === 'Connected' ? 'online' : 'offline'} label="SIGNALR" />
      </div>
      <div className="footer-center">
        <span className="footer-brand">VIEWOWL FLOW COMMAND CENTER</span>
        <span className="footer-version">v1.0.0</span>
      </div>
      <div className="footer-right">
        <span className="footer-ts">{ts}</span>
      </div>
    </footer>
  )
}
export default Footer
