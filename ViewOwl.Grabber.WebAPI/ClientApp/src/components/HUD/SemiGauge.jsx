import { memo } from 'react'

// SVG semicircular arc gauge — maps RSSI -100...-50 dBm onto a 180° arc.
// Dimensions use the golden ratio scale: viewBox 140×78 (≈ φ ratio 1.79),
// arc radius R=45, center CX=70 CY=54 (leaves room below for labels).

const CX = 70
const CY = 54
const R  = 45

// Maps RSSI dBm to a 0..1 fill fraction.
// -100 dBm → 0 (empty), -50 dBm → 1 (full); clipped at both ends.
const rssiToPct = (rssi) => {
  if (rssi == null) return 0
  return Math.max(0, Math.min(1, (rssi + 100) / 50))
}

// Pick arc stroke color matching the RSSI quality thresholds used by rssiInfo().
const arcColor = (rssi) => {
  if (rssi == null)  return 'rgba(0,229,255,0.18)'
  if (rssi >= -50)   return 'var(--primary)'
  if (rssi >= -67)   return '#80ff80'
  if (rssi >= -75)   return 'var(--warning)'
  return 'var(--danger)'
}

// Build SVG arc path for a fraction [0,1] of the 180° semicircle.
// Arc starts at the left endpoint (CX-R, CY) and sweeps clockwise over the top.
const valuePath = (pct) => {
  if (pct <= 0.001) return ''
  // Endpoint travels from left (angle π) to right (angle 0) as pct goes 0→1.
  const ex = CX - R * Math.cos(Math.PI * pct)
  const ey = CY - R * Math.sin(Math.PI * pct)
  const large = pct > 0.5 ? 1 : 0
  return `M ${CX - R},${CY} A ${R},${R} 0 ${large} 1 ${ex.toFixed(2)},${ey.toFixed(2)}`
}

// Tick marks at 0%, 50%, and 100% positions on the arc
const TICKS = [0, 0.5, 1]

const SemiGauge = ({ rssi, label = 'SIGNAL', isOnline = false }) => {
  const pct      = rssiToPct(rssi)
  const color    = arcColor(rssi)
  // Scanning state: device is online but no RSSI data received yet
  const scanning = rssi == null && isOnline

  return (
    <svg
      viewBox="0 0 140 78"
      width="100%"
      height="100%"
      overflow="hidden"
      xmlns="http://www.w3.org/2000/svg"
    >
      {/* Background track — full 180° */}
      <path
        d={`M ${CX - R},${CY} A ${R},${R} 0 1 1 ${CX + R},${CY}`}
        fill="none"
        stroke="rgba(0,229,255,0.15)"
        strokeWidth="5"
        strokeLinecap="round"
      />

      {/* Value arc */}
      {pct > 0.001 && (
        <path
          d={valuePath(pct)}
          fill="none"
          stroke={color}
          strokeWidth="5"
          strokeLinecap="round"
          style={{ filter: `drop-shadow(0 0 6px ${color}) drop-shadow(0 0 12px ${color})` }}
        />
      )}

      {/* Tick dots at -100, -75, -50 dBm positions */}
      {TICKS.map(t => {
        const r2 = R + 7
        const tx = CX - r2 * Math.cos(Math.PI * t)
        const ty = CY - r2 * Math.sin(Math.PI * t)
        return (
          <circle
            key={t}
            cx={tx.toFixed(2)}
            cy={ty.toFixed(2)}
            r="1.8"
            fill="rgba(0,229,255,0.35)"
          />
        )
      })}

      {/* Needle pip at current value */}
      {rssi != null && (
        <circle
          cx={(CX - (R - 3) * Math.cos(Math.PI * pct)).toFixed(2)}
          cy={(CY - (R - 3) * Math.sin(Math.PI * pct)).toFixed(2)}
          r="3"
          fill={color}
          style={{ filter: `drop-shadow(0 0 5px ${color})` }}
        />
      )}

      {/* Scanning pip — sweeps left→right while waiting for first RSSI */}
      {scanning && (
        <>
          <style>{`
            @keyframes sg-scan {
              0%   { rotate: 0deg; }
              100% { rotate: 180deg; }
            }
            .sg-scan-pip {
              transform-origin: ${CX}px ${CY}px;
              animation: sg-scan 1.6s ease-in-out infinite alternate;
            }
          `}</style>
          <g className="sg-scan-pip">
            <circle
              cx={(CX - (R - 3)).toFixed(2)}
              cy={CY.toFixed(2)}
              r="3"
              fill="rgba(0,229,255,0.6)"
              style={{ filter: 'drop-shadow(0 0 5px rgba(0,229,255,0.8))' }}
            />
          </g>
        </>
      )}

      {/* dBm readout */}
      <text
        x={CX}
        y={CY + 10}
        textAnchor="middle"
        fontFamily="var(--font)"
        fontSize="11"
        fill={rssi != null ? color : 'rgba(0,229,255,0.55)'}
        style={rssi != null ? { filter: `drop-shadow(0 0 4px ${color})` } : undefined}
      >
        {rssi != null ? `${rssi}` : scanning ? '···' : '—'}
      </text>

      {/* Label */}
      <text
        x={CX}
        y={CY + 22}
        textAnchor="middle"
        fontFamily="var(--font)"
        fontSize="6"
        fill="rgba(0,229,255,0.75)"
        letterSpacing="1.5"
      >
        {label}
      </text>

      {/* Scale endpoints */}
      <text x={CX - R - 2} y={CY + 9} textAnchor="end"
        fontFamily="var(--font)" fontSize="5" fill="rgba(0,229,255,0.55)">
        -100
      </text>
      <text x={CX + R + 2} y={CY + 9} textAnchor="start"
        fontFamily="var(--font)" fontSize="5" fill="rgba(0,229,255,0.55)">
        -50
      </text>
    </svg>
  )
}

export default memo(SemiGauge)
