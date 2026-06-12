import './DataRow.css'

// accent — cyan value glow  |  danger — red row (label + value)  |  success — green row (label + value)
function DataRow({ label, value, accent, danger, success }) {
  const rowCls = danger ? 'dr-row-danger' : success ? 'dr-row-success' : ''
  const valCls = accent ? 'dr-accent' : danger ? 'dr-danger' : success ? 'dr-success' : ''
  return (
    <div className={`data-row ${rowCls}`}>
      <span className="dr-label">{label}</span>
      <span className={`dr-value ${valCls}`}>{value ?? '—'}</span>
    </div>
  )
}
export default DataRow
