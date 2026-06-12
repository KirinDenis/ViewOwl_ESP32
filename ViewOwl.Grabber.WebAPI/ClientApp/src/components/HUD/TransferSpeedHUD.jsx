import { memo, useMemo } from 'react'

// Polar transfer-speed gauge + history list.
//
// Left: 24-petal polar chart, one petal per recent transfer.
//       Most recent = top (12 o'clock), earlier entries clockwise.
//       Petal length = normalised throughput (KB/s relative to session max).
//       Petal color: green=success, red=fail, amber=retried, dim=not_modified.
//
// Right: scrollable list of last 6 entries.
//
// Props
//   transferHistory  Array<TransferEntry>  (max 24, newest first)
//
// TransferEntry shape:
//   { time: Date, durationMs: number, sizeBytes: number|null,
//     flag: number|null,  // 0=raw, 3=lz4, 4=delta
//     retries: number, outcome: 'success'|'fail'|'not_modified' }

const SVG_SIZE   = 82         // svg viewBox dimension
const CX         = 41
const CY         = 41
const INNER_R    = 13
const MAX_R      = 37
const PETAL_W    = 5
const MAX_PETALS = 24

const flagLabel = (flag) => {
  if (flag === 0x03) return 'LZ4'
  if (flag === 0x04) return 'Δ'
  if (flag === 0x00) return 'RAW'
  return ''
}

const petalColor = (entry) => {
  if (!entry)                         return 'rgba(0,229,255,0.07)'
  if (entry.outcome === 'not_modified') return 'rgba(0,229,255,0.28)'
  if (entry.outcome === 'fail')         return 'rgba(255,51,51,0.75)'
  if (entry.retries > 0)               return 'rgba(255,200,0,0.65)'
  return 'rgba(0,229,136,0.75)'
}

const rowColor = (entry) => {
  if (entry.outcome === 'fail')          return '#ff4444'
  if (entry.outcome === 'not_modified')  return 'rgba(0,229,255,0.5)'
  if (entry.retries > 0)                 return '#ffc800'
  return '#00e566'
}

const fmt = (n) => n < 10 ? `0${n}` : `${n}`

const fmtTime = (d) => `${fmt(d.getHours())}:${fmt(d.getMinutes())}`

const fmtDuration = (ms) => {
  if (ms == null) return ''
  if (ms < 1000) return `${ms}ms`
  return `${(ms / 1000).toFixed(1)}s`
}

const fmtSize = (bytes) => {
  if (bytes == null) return ''
  if (bytes < 1024) return `${bytes}B`
  return `${(bytes / 1024).toFixed(0)}KB`
}

// Bar blocks (max 8 blocks), proportional to duration
const barBlocks = (ms, maxMs) => {
  if (ms == null || maxMs === 0) return 1
  const blocks = Math.max(1, Math.round((ms / maxMs) * 8))
  return Math.min(blocks, 8)
}

const TransferSpeedHUD = ({ transferHistory = [] }) => {
  const entries = transferHistory.slice(0, MAX_PETALS)
  const listEntries = transferHistory.slice(0, 6)

  const maxThroughput = useMemo(() => {
    const vals = entries
      .filter(e => e.outcome !== 'not_modified' && e.durationMs > 0 && e.sizeBytes)
      .map(e => (e.sizeBytes / 1024) / (e.durationMs / 1000))
    return Math.max(1, ...vals)
  }, [entries])

  const maxMs = useMemo(
    () => Math.max(1, ...listEntries.map(e => e.durationMs ?? 0)),
    [listEntries]
  )

  return (
    <div style={{ display: 'flex', alignItems: 'center', width: '100%', height: '100%' }}>

      {/* ── Polar chart ────────────────────────────────────────────────── */}
      <svg
        viewBox={`0 0 ${SVG_SIZE} ${SVG_SIZE}`}
        width={SVG_SIZE}
        height={SVG_SIZE}
        xmlns="http://www.w3.org/2000/svg"
        style={{ flexShrink: 0 }}
      >
        {/* Outer dashed ring */}
        <circle cx={CX} cy={CY} r={MAX_R}
          fill="none" stroke="rgba(0,229,255,0.18)" strokeWidth="1" strokeDasharray="2 4" />

        {/* Inner ring */}
        <circle cx={CX} cy={CY} r={INNER_R}
          fill="none" stroke="rgba(0,229,255,0.4)" strokeWidth="1" />

        {/* Petals — index 0 = most recent = top, clockwise */}
        {Array.from({ length: MAX_PETALS }, (_, i) => {
          const entry   = entries[i] ?? null
          const angle   = (i / MAX_PETALS) * 2 * Math.PI - Math.PI / 2
          const color   = petalColor(entry)

          let lenFrac = 0.08  // minimal stub for empty slots
          if (entry) {
            if (entry.outcome === 'not_modified') {
              lenFrac = 0.15
            } else if (entry.sizeBytes && entry.durationMs > 0) {
              const kbps = (entry.sizeBytes / 1024) / (entry.durationMs / 1000)
              lenFrac = Math.max(0.12, kbps / maxThroughput)
            } else {
              lenFrac = 0.15
            }
          }

          const outerR = INNER_R + (MAX_R - INNER_R) * lenFrac
          const x1 = CX + INNER_R * Math.cos(angle)
          const y1 = CY + INNER_R * Math.sin(angle)
          const x2 = CX + outerR  * Math.cos(angle)
          const y2 = CY + outerR  * Math.sin(angle)

          return (
            <line key={i}
              x1={x1.toFixed(2)} y1={y1.toFixed(2)}
              x2={x2.toFixed(2)} y2={y2.toFixed(2)}
              stroke={color}
              strokeWidth={i === 0 && entry ? PETAL_W + 1 : PETAL_W}
              strokeLinecap="round"
              style={i === 0 && entry ? { filter: `drop-shadow(0 0 3px ${color})` } : undefined}
            />
          )
        })}

        {/* Centre: avg KB/s */}
        {entries.length > 0 && (() => {
          const valid = entries.filter(e => e.sizeBytes && e.durationMs > 0 && e.outcome !== 'not_modified')
          if (valid.length === 0) return null
          const avg = valid.reduce((s, e) => s + (e.sizeBytes / 1024) / (e.durationMs / 1000), 0) / valid.length
          return (
            <>
              <text x={CX} y={CY + 2} textAnchor="middle"
                fontFamily="var(--font)" fontSize="7" fill="rgba(0,229,255,0.9)" letterSpacing="0.5">
                {avg < 100 ? avg.toFixed(1) : Math.round(avg)}
              </text>
              <text x={CX} y={CY + 10} textAnchor="middle"
                fontFamily="var(--font)" fontSize="4.5" fill="rgba(0,229,255,0.65)" letterSpacing="0.5">
                KB/S
              </text>
            </>
          )
        })()}
      </svg>

      {/* ── History list ───────────────────────────────────────────────── */}
      <div style={{
        flex: 1,
        minWidth: 0,
        padding: '2px 6px',
        display: 'flex',
        flexDirection: 'column',
        gap: '2px',
        overflow: 'hidden',
      }}>
        {listEntries.length === 0 && (
          <span style={{ fontSize: '0.48rem', color: 'rgba(0,229,255,0.25)', fontFamily: 'var(--font)', textAlign: 'center', marginTop: '8px' }}>
            NO TRANSFERS YET
          </span>
        )}
        {listEntries.map((e, i) => {
          const color   = rowColor(e)
          const blocks  = barBlocks(e.durationMs, maxMs)
          const barStr  = '█'.repeat(blocks) + '░'.repeat(8 - blocks)
          const label   = e.outcome === 'not_modified'
            ? 'NOT_MOD'
            : [fmtSize(e.sizeBytes), flagLabel(e.flag)].filter(Boolean).join(' ')
          const retryStr = e.retries > 0 ? ` ⚡×${e.retries}` : ''
          const durStr   = fmtDuration(e.durationMs)
          const icon     = e.outcome === 'fail' ? '✗' : e.outcome === 'not_modified' ? '~' : '✓'

          return (
            <div key={i} style={{
              display: 'flex',
              alignItems: 'baseline',
              gap: '3px',
              fontSize: '0.48rem',
              fontFamily: 'var(--font)',
              lineHeight: 1.25,
              minWidth: 0,
              overflow: 'hidden',
            }}>
              <span style={{ color: 'rgba(0,229,255,0.7)', flexShrink: 0 }}>
                {fmtTime(e.time)}
              </span>
              <span style={{ color: 'rgba(0,229,255,0.35)', flexShrink: 0, fontSize: '0.4rem' }}>
                {barStr}
              </span>
              <span style={{ color, flexShrink: 0 }}>
                {durStr}{retryStr}
              </span>
              <span style={{ color: 'rgba(0,229,255,0.75)', flexShrink: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {label}
              </span>
              <span style={{ color, flexShrink: 0 }}>{icon}</span>
            </div>
          )
        })}
      </div>
    </div>
  )
}

export default memo(TransferSpeedHUD)
