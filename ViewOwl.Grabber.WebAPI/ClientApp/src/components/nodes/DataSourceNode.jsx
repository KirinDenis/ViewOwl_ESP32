import { memo } from 'react'
import { Handle, Position } from 'reactflow'
import './DataSourceNode.css'

const DataSourceNode = ({ data }) => {
  return (
    <div className="datasource-node">
      <div className="node-header datasource-header">
        <span className="node-icon">🌐</span>
        <span className="node-title">{data.name ?? 'Data Source'}</span>
      </div>

      <div className="node-body">
        {data.url && (
          <div className="node-stat">
            <span className="stat-label">URL</span>
            <span className="stat-value url-value">{data.url}</span>
          </div>
        )}
      </div>

      <div className="node-footer">
        <span className="node-type">Source</span>
      </div>

      <Handle
        type="source"
        position={Position.Right}
        id="output"
        className="handle-output"
      />
    </div>
  )
}

export default memo(DataSourceNode)
