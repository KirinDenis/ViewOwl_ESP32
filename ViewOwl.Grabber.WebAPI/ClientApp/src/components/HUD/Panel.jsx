import './Panel.css'

function Panel({ title, children, className = '' }) {
  return (
    <div className={`hud-panel panel-box ${className}`}>
      {title && <div className="panel-title">{title}</div>}
      <div className="panel-content">{children}</div>
    </div>
  )
}
export default Panel
