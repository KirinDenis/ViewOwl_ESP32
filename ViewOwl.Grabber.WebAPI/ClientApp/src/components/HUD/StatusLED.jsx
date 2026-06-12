import './StatusLED.css'

function StatusLED({ status, label }) {
  const cls = status === 'online' || status === 'Connected' ? 'led-green'
            : status === 'warning' || status === 'Reconnecting' ? 'led-yellow'
            : status === 'offline' || status === 'Disconnected' || status === 'Failed' ? 'led-red'
            : 'led-dim'
  return (
    <div className="status-led">
      <span className={`led ${cls}`} />
      {label && <span className="led-label">{label}</span>}
    </div>
  )
}
export default StatusLED
