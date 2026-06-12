// SVG mini line chart for ping history.
// data: array of { timestamp, success } objects (from /api/devices/{id}/stats)
// Renders a timeline bar chart: green tick = success, red tick = failed ping

const W = 400
const H = 60

const MiniLineChart = ({ pings = [], maxDisplay = 60 }) => {
  if (!pings || pings.length === 0) {
    return (
      <div className="mini-chart-empty">NO DATA</div>
    )
  }

  // Keep last maxDisplay pings
  const visible = pings.slice(-maxDisplay)
  const barW = W / maxDisplay
  const barGap = 1

  return (
    <svg
      viewBox={`0 0 ${W} ${H}`}
      width="100%"
      height={H}
      className="mini-line-chart"
      preserveAspectRatio="none"
    >
      {/* Grid lines */}
      <line x1="0" y1={H * 0.25} x2={W} y2={H * 0.25} stroke="rgba(77,208,225,0.1)" strokeWidth="1" />
      <line x1="0" y1={H * 0.5}  x2={W} y2={H * 0.5}  stroke="rgba(77,208,225,0.1)" strokeWidth="1" />
      <line x1="0" y1={H * 0.75} x2={W} y2={H * 0.75} stroke="rgba(77,208,225,0.1)" strokeWidth="1" />

      {/* Ping bars */}
      {visible.map((ping, i) => {
        const x = i * barW + barGap / 2
        const w = Math.max(barW - barGap, 1)
        const color = ping.success ? 'rgba(0,255,136,0.7)' : 'rgba(255,51,51,0.7)'
        const glowColor = ping.success ? 'rgba(0,255,136,0.3)' : 'rgba(255,51,51,0.3)'
        const barH = ping.success ? H * 0.75 : H * 0.4
        return (
          <rect
            key={i}
            x={x}
            y={H - barH}
            width={w}
            height={barH}
            fill={color}
            opacity={0.8}
            style={{ filter: `drop-shadow(0 0 2px ${glowColor})` }}
          />
        )
      })}

      {/* Success rate label */}
      {visible.length > 0 && (() => {
        const successRate = (visible.filter(p => p.success).length / visible.length * 100).toFixed(0)
        return (
          <text x={W - 2} y={10} textAnchor="end" fill="rgba(0,255,136,0.6)" fontSize="8" fontFamily="monospace">
            {successRate}% OK
          </text>
        )
      })()}
    </svg>
  )
}

export default MiniLineChart
