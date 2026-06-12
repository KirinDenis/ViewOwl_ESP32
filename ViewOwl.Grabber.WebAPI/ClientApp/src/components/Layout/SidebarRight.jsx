import Panel from '../HUD/Panel'
import StatusLED from '../HUD/StatusLED'
import DataRow from '../HUD/DataRow'
import './SidebarRight.css'

function SidebarRight({ selectedNode, collapsed, onToggle }) {
  return (
    <aside className={`sidebar-right${collapsed ? ' sidebar-collapsed' : ''}`}>
      <button className="sidebar-collapse-btn sidebar-collapse-btn-right" onClick={onToggle} title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}>
        {collapsed ? '◀' : '▶'}
      </button>

      {!collapsed && (
        !selectedNode
          ? (
            <Panel title="NODE INSPECTOR">
              <p className="sidebar-empty">SELECT A NODE<br/>TO INSPECT</p>
            </Panel>
          )
          : (() => {
              const { type, data } = selectedNode
              return (
                <Panel title={`${type.toUpperCase()} NODE`}>
                  <DataRow label="ID"   value={`#${data.id}`} accent />
                  <DataRow label="Name" value={data.name} />

                  {type === 'device' && <>
                    <div className="inspector-section">
                      <StatusLED
                        status={data.status === 'Online' ? 'online' : 'offline'}
                        label={data.status}
                      />
                    </div>
                    <DataRow label="IP Address"  value={data.ipAddress} />
                    <DataRow label="Display"     value={data.displayWidth && data.displayHeight ? `${data.displayWidth}x${data.displayHeight}` : null} />
                    <DataRow label="Display Type" value={data.displayType} />
                    <DataRow label="Firmware"    value={data.firmwareVersion} />
                    <DataRow label="Last Seen"   value={data.lastSeenAt ? new Date(data.lastSeenAt).toLocaleTimeString() : null} />
                  </>}

                  {type === 'grabber' && <>
                    <DataRow label="Created" value={data.createdAt ? new Date(data.createdAt).toLocaleDateString() : null} />
                    <DataRow label="Type"    value="HTML Template" />
                  </>}

                  {type === 'dataSource' && <>
                    <DataRow label="URL" value={data.url} />
                  </>}
                </Panel>
              )
            })()
      )}
    </aside>
  )
}
export default SidebarRight
