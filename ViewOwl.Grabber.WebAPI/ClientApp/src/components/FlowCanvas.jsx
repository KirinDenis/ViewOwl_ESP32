import { useCallback, useState, useEffect, useRef } from 'react'
import ReactFlow, {
  MiniMap,
  Controls,
  Background,
  useNodesState,
  useEdgesState,
  addEdge,
  BackgroundVariant,
} from 'reactflow'
import 'reactflow/dist/style.css'

import GrabberNode from './nodes/GrabberNode'
import DeviceNode from './nodes/DeviceNode'
import DataSourceNode from './nodes/DataSourceNode'
import Toast from './Toast'
import KnockingPanel from './KnockingPanel'
import { useSignalR } from '../hooks/useSignalR'
import { authFetch } from '../utils/auth'
import { confirm } from '../utils/confirm'
import './FlowCanvas.css'

const nodeTypes = {
  grabber: GrabberNode,
  device: DeviceNode,
  dataSource: DataSourceNode,
}

// STALE_KNOCK_MS: knocking entries are pruned if no new knock arrives within 5 minutes.
const STALE_KNOCK_MS = 5 * 60 * 1000

const NODE_POSITIONS_KEY = 'viewowl:node-positions'
const SNAP_GRID = [20, 20]

function loadNodePositions() {
  try {
    return JSON.parse(localStorage.getItem(NODE_POSITIONS_KEY) || '{}')
  } catch {
    return {}
  }
}

function saveNodePosition(nodeId, position) {
  try {
    const all = loadNodePositions()
    all[nodeId] = position
    localStorage.setItem(NODE_POSITIONS_KEY, JSON.stringify(all))
  } catch {
    // localStorage unavailable — silently ignore
  }
}

function FlowCanvas({ dashboardState, onNodeSelect, onConnectionStateChange, onSignalREvent, onRefreshDashboard, onTemplateDeleted, onDeviceDeleted, onDeviceRegistered }) {
  const [nodes, setNodes, onNodesChange] = useNodesState([])
  const [edges, setEdges, onEdgesChange] = useEdgesState([])
  const [toasts, setToasts] = useState([])
  const [knockingDevices, setKnockingDevices] = useState([])  // devices trying to connect but failing

  // Positions loaded from the server on mount; merged with localStorage on node builds.
  const serverPositionsRef = useRef({})

  // Tracks last-known status per device to suppress duplicate "is now Online" toasts.
  const prevStatusRef = useRef({})

  // Ref to current nodes so SignalR handlers can read device statuses without stale closures.
  const nodesRef = useRef([])
  useEffect(() => { nodesRef.current = nodes }, [nodes])

  // Debounce timer — batches rapid drag events into a single server PUT.
  const dragSaveTimerRef = useRef(null)

  // Load positions from server once on mount; apply to any nodes already rendered.
  useEffect(() => {
    authFetch('/api/flow-layout')
      .then(res => (res?.ok ? res.json() : null))
      .then(data => {
        if (!data?.positions || Object.keys(data.positions).length === 0) return
        serverPositionsRef.current = data.positions
        setNodes(nds =>
          nds.map(node => {
            const pos = data.positions[node.id]
            return pos ? { ...node, position: { x: pos.x, y: pos.y } } : node
          })
        )
        try {
          localStorage.setItem(NODE_POSITIONS_KEY, JSON.stringify(data.positions))
        } catch {
          // localStorage unavailable — silently ignore
        }
      })
      .catch(() => {})
  }, [setNodes]) // intentionally runs once on mount

  // Convert dashboard state to React Flow nodes and edges.
  // Node positions prefer server-synced values, then localStorage, then default layout.
  useEffect(() => {
    if (!dashboardState) return

    const newNodes = []
    const newEdges = []
    // Merge: server positions override localStorage (server is the source of truth after first sync)
    const savedPositions = { ...loadNodePositions(), ...serverPositionsRef.current }

    // Track which template nodes were visible in the previous render cycle.
    // Used to keep already-placed nodes stable across dashboard reloads without
    // re-triggering the flood-prevention filter.
    const prevTemplateNodeIds = new Set(
      nodesRef.current.filter(n => n.type === 'grabber').map(n => n.id)
    )

    // Separate counter for nodes placed without a saved position so fallback Y
    // is based on how many unsaved nodes preceded this one — not the raw list
    // index (which can be 50+, pushing new nodes thousands of pixels off-screen).
    let templateNewIdx = 0
    dashboardState.templates?.forEach((template) => {
      const nodeId = `template-${template.id}`

      // A template node appears on the canvas only when at least one of these is true:
      //   1. User has placed it there before (saved position in localStorage / server)
      //   2. It has an active connection to a device
      //   3. It was already visible in the previous render (keep stable across reloads)
      // This prevents bulk-imported or freshly-created templates from flooding the canvas.
      const hasSavedPosition = !!savedPositions[nodeId]
      const hasConnection    = dashboardState.connections?.some(c => c.templateId === template.id) ?? false
      const wasOnCanvas      = prevTemplateNodeIds.has(nodeId)
      if (!hasSavedPosition && !hasConnection && !wasOnCanvas) return

      const position = savedPositions[nodeId] ?? { x: 100, y: 100 + templateNewIdx * 320 }
      if (!savedPositions[nodeId]) templateNewIdx++

      newNodes.push({
        id: nodeId,
        type: 'grabber',
        position,
        data: {
          id: template.id,
          name: template.name,
          createdAt: template.createdAt,
          onDelete: async () => {
            if (!await confirm(`Delete template "${template.name}"?`, { title: 'DELETE TEMPLATE', danger: true })) return
            const res = await authFetch(`/api/templates/${template.id}`, { method: 'DELETE' })
            if (res?.ok) {
              // Local updates only — no full dashboard reload needed.
              // onRefreshDashboard would rebuild all nodes and lose the viewport position.
              setNodes(nds => nds.filter(n => n.id !== nodeId))
              setEdges(eds => eds.filter(e => e.source !== nodeId))
              onTemplateDeleted?.(template.id)
            } else {
              addToast('Failed to delete template', 'error')
            }
          },
        },
      })
    })

    let deviceNewIdx = 0
    dashboardState.devices?.forEach((device) => {
      const nodeId = `device-${device.id}`
      const devicePosition = savedPositions[nodeId] ?? { x: 620, y: 100 + deviceNewIdx * 320 }
      if (!savedPositions[nodeId]) deviceNewIdx++
      newNodes.push({
        id: nodeId,
        type: 'device',
        position: devicePosition,
        data: {
          id: device.id,
          name: device.name,
          token: device.token,
          displayType: device.displayType,
          displayWidth: device.displayWidth,
          displayHeight: device.displayHeight,
          firmwareVersion: device.firmwareVersion,
          status: device.status,
          lastSeenAt: device.lastSeenAt,
          ipAddress: device.ipAddress,
          activeTemplateId: device.activeTemplateId,
          activeTemplateName: device.activeTemplateName,
          onDelete: async () => {
            if (!await confirm(`Delete device "${device.name}"?`, { title: 'DELETE DEVICE', danger: true })) return
            const res = await authFetch(`/api/devices/${device.id}`, { method: 'DELETE' })
            if (res?.ok) {
              // Local updates only — avoid full dashboard reload so viewport is preserved.
              setNodes(nds => nds.filter(n => n.id !== nodeId))
              setEdges(eds => eds.filter(e => e.target !== nodeId))
              onDeviceDeleted?.(device.id)
            } else {
              addToast('Failed to delete device', 'error')
            }
          },
          onNameSaved: (newName) => {
            setNodes(nds => nds.map(n =>
              n.id === nodeId ? { ...n, data: { ...n.data, name: newName } } : n
            ))
          },
        },
      })
    })

    dashboardState.connections?.forEach((connection) => {
      newEdges.push({
        id: `edge-${connection.id}`,
        source: `template-${connection.templateId}`,
        target: `device-${connection.deviceId}`,
        animated: true,
        style: { stroke: '#00e5ff', strokeWidth: 2 },
        data: { deviceId: connection.deviceId },
      })
    })

    setNodes(newNodes)
    setEdges(newEdges)
  }, [dashboardState, setNodes, setEdges])

  const addToast = useCallback((message, type = 'info') => {
    const id = Date.now()
    setToasts((prev) => [...prev, { id, message, type }])
  }, [])

  const removeToast = useCallback((id) => {
    setToasts((prev) => prev.filter((t) => t.id !== id))
  }, [])

  const dismissKnocking = useCallback((ip) => {
    setKnockingDevices(prev => prev.filter(d => d.ip !== ip))
  }, [])

  // Register an unknown device that is knocking — uses token from HELLO packet.
  // Display type must be supplied by the user in KnockingPanel because the
  // HELLO payload display info is not yet forwarded through the notification chain.
  const handleRegisterKnocking = useCallback(async (d, displayId, name) => {
    const DISPLAY_TYPES = {
      '320x240-ili': { w: 320, h: 240, type: 'ILI9341' },
      '480x320-st':  { w: 480, h: 320, type: 'ST7796'  },
    }
    const disp = DISPLAY_TYPES[displayId] ?? DISPLAY_TYPES['480x320-st']
    const res = await authFetch('/api/devices', {
      method:  'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        name,
        token:        d.token,
        displayWidth:  disp.w,
        displayHeight: disp.h,
        displayType:   disp.type,
      }),
    })
    if (res?.ok) {
      dismissKnocking(d.ip)
      onRefreshDashboard?.()
      addToast('Device registered — it will appear on the canvas shortly', 'success')
    } else if (res?.status === 409) {
      addToast('A device with this name already exists', 'error')
    } else {
      addToast('Failed to register device', 'error')
    }
  }, [dismissKnocking, onRefreshDashboard, addToast])

  // Prune stale knocking entries every 30 seconds
  useEffect(() => {
    const interval = setInterval(() => {
      const cutoff = Date.now() - STALE_KNOCK_MS
      setKnockingDevices(prev => prev.filter(d => new Date(d.lastSeen).getTime() > cutoff))
    }, 30000)
    return () => clearInterval(interval)
  }, [])

  // SignalR event handler — updates nodes in real-time and handles cross-session sync events
  const handleSignalREvent = useCallback(
    (eventType, data) => {
      // Propagate to parent for sidebar event log
      onSignalREvent?.(eventType, data)

      switch (eventType) {
        case 'DeviceStatusChanged':
          setNodes((nds) =>
            nds.map((node) => {
              if (node.id === `device-${data.deviceId}`) {
                // Only toast on a real status transition — suppress duplicate events
                if (prevStatusRef.current[data.deviceId] !== data.status) {
                  addToast(
                    `${node.data.name} is now ${data.status}`,
                    data.status === 'Online' ? 'success' : 'warning'
                  )
                  prevStatusRef.current[data.deviceId] = data.status
                }
                return {
                  ...node,
                  data: {
                    ...node.data,
                    status: data.status,
                    lastSeenAt: data.lastSeenAt,
                    ipAddress: data.ipAddress ?? node.data.ipAddress,
                  },
                }
              }
              return node
            })
          )
          break

        case 'DevicePing':
          // Forward the full ping event so DeviceNode can update uptime + RSSI live
          setNodes((nds) =>
            nds.map((node) => {
              if (node.id === `device-${data.deviceId}`) {
                return {
                  ...node,
                  data: {
                    ...node.data,
                    lastSeenAt: data.timestamp,
                    pingEvent: data,
                  },
                }
              }
              return node
            })
          )
          break

        case 'GrabCompleted':
          // Push grab event into the matching template node so GrabberNode can refresh its stats
          setNodes(nds =>
            nds.map(node =>
              node.id === `template-${data.templateId}`
                ? { ...node, data: { ...node.data, grabEvent: data } }
                : node
            )
          )
          break

        case 'FlowLayoutChanged': {
          // Another session saved new positions — apply them to our canvas.
          const positions = data.positions ?? {}
          if (Object.keys(positions).length === 0) break
          serverPositionsRef.current = positions
          setNodes(nds =>
            nds.map(node => {
              const pos = positions[node.id]
              return pos ? { ...node, position: { x: pos.x, y: pos.y } } : node
            })
          )
          try {
            localStorage.setItem(NODE_POSITIONS_KEY, JSON.stringify(positions))
          } catch {
            // localStorage unavailable — silently ignore
          }
          break
        }

        case 'ConnectionsChanged': {
          // Another session created or deleted a connection — rebuild edges from authoritative list.
          const connections = data.connections ?? []
          const deviceStatusMap = {}
          nodesRef.current.forEach(n => {
            if (n.type === 'device') deviceStatusMap[n.data.id] = n.data.status
          })
          const newEdges = connections.map(conn => ({
            id: `edge-${conn.id}`,
            source: `template-${conn.templateId}`,
            target: `device-${conn.deviceId}`,
            animated: true,
            style: { stroke: '#00e5ff', strokeWidth: 2 },
            data: { deviceId: conn.deviceId },
          }))
          setEdges(newEdges)
          break
        }

        case 'DeviceKnocking': {
          // Add or update the knocking device entry; show a toast only on first knock.
          const ip = data.ipAddress
          setKnockingDevices(prev => {
            const existing = prev.find(d => d.ip === ip)
            if (existing) {
              return prev.map(d => d.ip === ip
                ? { ...d, count: d.count + 1, lastSeen: data.timestamp, reason: data.reason,
                    deviceId: data.deviceId, deviceName: data.deviceName }
                : d
              )
            }
            addToast(
              `Device knocking: ${ip}${data.deviceName ? ` (${data.deviceName})` : ''} — ${data.reason}`,
              'warning'
            )
            return [...prev, {
              ip,
              token:      data.token,
              reason:     data.reason,
              deviceId:   data.deviceId,
              deviceName: data.deviceName,
              count:      1,
              firstSeen:  data.timestamp,
              lastSeen:   data.timestamp,
            }]
          })
          break
        }

        case 'DeviceTransferStarted':
          // Device is actively receiving DATA packets — show send status on its node.
          setNodes(nds => nds.map(node =>
            node.id === `device-${data.deviceId}`
              ? { ...node, data: { ...node.data, deliveryEvent: {
                  phase:          'send',
                  imageSizeBytes: data.imageSizeBytes,
                  fileFlag:       data.fileFlag ?? null,
                  ts:             Date.now(),
                }}}
              : node
          ))
          break

        case 'DeviceTransferProgress':
          // In-flight progress — forward to the node for optional indicator.
          setNodes(nds => nds.map(node =>
            node.id === `device-${data.deviceId}`
              ? { ...node, data: { ...node.data, progressEvent: data } }
              : node
          ))
          break

        case 'DeviceGrabCompleted': {
          // Transfer finished — resolve outcome and record into transfer history.
          const outcome = data.outcome ?? (data.success ? 'success' : 'fail')
          const phase   = outcome === 'not_modified' ? 'not_modified'
                        : outcome === 'success'      ? 'success'
                        : 'fail'
          setNodes(nds => nds.map(node =>
            node.id === `device-${data.deviceId}`
              ? { ...node, data: { ...node.data, deliveryEvent: {
                  phase,
                  durationMs:     data.durationMs     ?? null,
                  errorMessage:   data.errorMessage   ?? null,
                  imageSizeBytes: data.imageSizeBytes  ?? null,
                  retries:        data.retries         ?? 0,
                  ts:             Date.now(),
                }}}
              : node
          ))
          break
        }

        case 'TemplateUpdated':
        case 'GrabberUpdate':
          break

        default:
          break
      }
    },
    [setNodes, setEdges, addToast, onSignalREvent, setKnockingDevices]
  )

  const { connectionState } = useSignalR(handleSignalREvent)

  // Notify parent when connection state changes
  useEffect(() => {
    onConnectionStateChange?.(connectionState)
  }, [connectionState, onConnectionStateChange])

  const onNodeClick = useCallback((_evt, node) => {
    onNodeSelect?.({ type: node.type, data: node.data })
  }, [onNodeSelect])

  // Only allow template → device connections; block widget-widget and device-device drags
  const isValidConnection = useCallback((connection) => {
    return (
      connection.source?.startsWith('template-') &&
      connection.target?.startsWith('device-')
    )
  }, [])

  const onConnect = useCallback(
    (params) => {
      if (!params.source?.startsWith('template-') || !params.target?.startsWith('device-')) return

      const templateId = parseInt(params.source.split('-')[1])
      const deviceId   = parseInt(params.target.split('-')[1])

      // Remove any existing edge pointing to this device (one device = one widget rule)
      // The API will also replace the backend connection atomically
      setEdges(eds => eds.filter(e => e.target !== params.target))

      authFetch('/api/connections', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ templateId, deviceId }),
      })
        .then(res => {
          if (!res.ok) throw new Error(`HTTP ${res.status}`)
          return res.json()
        })
        .then(data => {
          setEdges(eds =>
            addEdge(
              {
                ...params,
                id: `edge-${data.id}`,
                animated: true,
                style: { stroke: '#00e5ff', strokeWidth: 2 },
                data: { deviceId },
              },
              eds
            )
          )
          addToast('Widget assigned to controller', 'success')
        })
        .catch(err => {
          console.error('Failed to create connection:', err)
          addToast('Failed to assign widget', 'error')
        })
    },
    [setEdges, addToast]
  )

  // Persist node position after each drag: localStorage immediately, server debounced 500 ms.
  const onNodeDragStop = useCallback((_evt, node) => {
    saveNodePosition(node.id, node.position)
    serverPositionsRef.current = { ...serverPositionsRef.current, [node.id]: node.position }

    if (dragSaveTimerRef.current) clearTimeout(dragSaveTimerRef.current)
    dragSaveTimerRef.current = setTimeout(() => {
      const positions = loadNodePositions()
      authFetch('/api/flow-layout', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ positions }),
      }).catch(() => {})
    }, 500)
  }, [])

  // Click on an edge to disconnect — no confirm dialog, shows toast instead
  const onEdgeClick = useCallback(
    (_event, edge) => {
      const connectionId = parseInt(edge.id.split('-')[1])

      authFetch(`/api/connections/${connectionId}`, { method: 'DELETE' })
        .then(res => {
          if (!res.ok) throw new Error(`HTTP ${res.status}`)
          setEdges(eds => eds.filter(e => e.id !== edge.id))
          addToast('Widget disconnected — click edge to reconnect', 'info')
        })
        .catch(err => {
          console.error('Failed to delete connection:', err)
          addToast('Failed to disconnect', 'error')
        })
    },
    [setEdges, addToast]
  )

  return (
    <div className="flow-canvas">
      <div className={`signalr-indicator ${connectionState.toLowerCase()}`}>
        <span className="indicator-dot" />
        <span className="indicator-text">{connectionState}</span>
      </div>

      <ReactFlow
        nodes={nodes}
        edges={edges}
        onNodesChange={onNodesChange}
        onEdgesChange={onEdgesChange}
        onConnect={onConnect}
        onEdgeClick={onEdgeClick}
        onNodeClick={onNodeClick}
        onNodeDragStop={onNodeDragStop}
        isValidConnection={isValidConnection}
        nodeTypes={nodeTypes}
        snapToGrid
        snapGrid={SNAP_GRID}
        fitView
        className="react-flow-dark"
      >
        <Controls />
        <MiniMap
          nodeColor={(node) => {
            switch (node.type) {
              case 'grabber':    return '#5a5a88'
              case 'device':     return '#4a4a6a'
              case 'dataSource': return '#3a3a55'
              default:           return '#22223a'
            }
          }}
        />
        <Background variant={BackgroundVariant.Dots} gap={24} size={2} color="rgba(160,160,204,0.55)" />
      </ReactFlow>

      {/* Notification column — fixed to the right sidebar strip, never over the canvas */}
      <div className="notification-column">
        <KnockingPanel devices={knockingDevices} onDismiss={dismissKnocking} onRegister={handleRegisterKnocking} />
        <div className="toast-container">
          {toasts.map((toast) => (
            <Toast
              key={toast.id}
              message={toast.message}
              type={toast.type}
              onClose={() => removeToast(toast.id)}
            />
          ))}
        </div>
      </div>
    </div>
  )
}

export default FlowCanvas
