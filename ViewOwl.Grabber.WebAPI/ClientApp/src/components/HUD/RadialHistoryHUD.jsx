import { memo } from 'react'

// 48-petal polar chart representing 24 hours of device ping history.
// One petal per 30-minute slot (slot 0 = 00:00, slot 47 = 23:30).
// Midnight is at the top (-π/2) and time advances clockwise.
//
// Petal length encodes activity (ping count normalised to the busiest slot).
// Petal color encodes health:
//   white  — current 30-min slot
//   cyan   — pings only, no errors
//   amber  — some transfer failures (errorCount > 0)
//   red    — majority failed (errorCount > count/2)
//   dim    — no data

const CX = 70
const CY = 70
const INNER_R   = 22   // --gs-3 ≈ 21px
const MAX_OUTER_R = 60
const STROKE_W  = 4    // base petal width
const CURRENT_W = 6    // wider stroke for the active slot

const slotColor = (slot, isCurrent) => {
  if (isCurrent) return 'var(--primary)'
  if (!slot || slot.count === 0) return 'rgba(0,229,255,0.08)'
  if (slot.errorCount > slot.count * 0.5) return 'rgba(255,51,51,0.7)'
  if (slot.errorCount > 0) return 'rgba(255,200,0,0.6)'
  return 'rgba(0,229,136,0.5)'
}

const RadialHistoryHUD = ({ pingHistory }) => {
  // Determine current 30-min slot index
  const now = new Date()
  const currentSlot = Math.floor((now.getHours() * 60 + now.getMinutes()) / 30)

  // Normalise petal lengths to the busiest slot so the chart fills the space.
  const maxCount = Math.max(1, ...pingHistory.map(s => s?.count ?? 0))

  return (
    <svg
      viewBox="0 0 140 140"
      width="100%"
      height="100%"
      xmlns="http://www.w3.org/2000/svg"
    >
      {/* Outer reference ring (dashed) */}
      <circle
        cx={CX} cy={CY} r={MAX_OUTER_R}
        fill="none"
        stroke="rgba(0,229,255,0.18)"
        strokeWidth="1"
        strokeDasharray="2 5"
      />

      {/* Inner ring */}
      <circle
        cx={CX} cy={CY} r={INNER_R}
        fill="none"
        stroke="rgba(0,229,255,0.4)"
        strokeWidth="1"
      />

      {/* Cardinal hour marks at 0h, 6h, 12h, 18h */}
      {[0, 12, 24, 36].map(slotIdx => {
        const angle = (slotIdx / 48) * 2 * Math.PI - Math.PI / 2
        const mx = CX + (MAX_OUTER_R + 7) * Math.cos(angle)
        const my = CY + (MAX_OUTER_R + 7) * Math.sin(angle)
        const labels = { 0: '0h', 12: '6h', 24: '12h', 36: '18h' }
        return (
          <text
            key={slotIdx}
            x={mx.toFixed(1)}
            y={my.toFixed(1)}
            textAnchor="middle"
            dominantBaseline="central"
            fontFamily="var(--font)"
            fontSize="5.5"
            fill="rgba(0,229,255,0.65)"
          >
            {labels[slotIdx]}
          </text>
        )
      })}

      {/* Petals */}
      {Array.from({ length: 48 }, (_, i) => {
        const slot      = pingHistory[i]
        const isCurrent = i === currentSlot
        const angle     = (i / 48) * 2 * Math.PI - Math.PI / 2

        const count = slot?.count ?? 0
        // Current slot always shows a minimal stub so it's visible even with no data.
        const lenFrac = isCurrent
          ? Math.max(0.12, count / maxCount)
          : count / maxCount
        const outerR  = INNER_R + (MAX_OUTER_R - INNER_R) * lenFrac

        const x1 = CX + INNER_R  * Math.cos(angle)
        const y1 = CY + INNER_R  * Math.sin(angle)
        const x2 = CX + outerR   * Math.cos(angle)
        const y2 = CY + outerR   * Math.sin(angle)

        const color   = slotColor(slot, isCurrent)
        const opacity = (!slot || slot.count === 0) && !isCurrent ? 0.5 : 1

        return (
          <line
            key={i}
            x1={x1.toFixed(2)} y1={y1.toFixed(2)}
            x2={x2.toFixed(2)} y2={y2.toFixed(2)}
            stroke={color}
            strokeWidth={isCurrent ? CURRENT_W : STROKE_W}
            opacity={opacity}
            strokeLinecap="round"
            style={isCurrent ? { filter: 'drop-shadow(0 0 3px rgba(255,255,255,0.8))' } : undefined}
          />
        )
      })}

      {/* Centre label */}
      <text
        x={CX} y={CY + 3}
        textAnchor="middle"
        fontFamily="var(--font)"
        fontSize="7.5"
        fill="rgba(0,229,255,0.85)"
        letterSpacing="1"
      >
        24H
      </text>
      <text
        x={CX} y={CY + 13}
        textAnchor="middle"
        fontFamily="var(--font)"
        fontSize="5"
        fill="rgba(0,229,255,0.55)"
        letterSpacing="1"
      >
        LOG
      </text>
    </svg>
  )
}

export default memo(RadialHistoryHUD)
