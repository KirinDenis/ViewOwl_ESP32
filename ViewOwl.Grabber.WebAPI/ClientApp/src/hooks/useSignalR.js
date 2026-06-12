import { useEffect, useRef, useState } from 'react'
import * as signalR from '@microsoft/signalr'
import { getToken } from '../utils/auth'

/**
 * Custom hook for SignalR connection management.
 * Connects to the dashboard hub and provides event handlers.
 */
export function useSignalR(onEvent) {
  const connectionRef = useRef(null)
  const [connectionState, setConnectionState] = useState('Disconnected')

  useEffect(() => {
    const connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/viewowl', {
        accessTokenFactory: () => getToken() ?? '',
        skipNegotiation: true,
        transport: signalR.HttpTransportType.WebSockets,
      })
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (retryContext) => {
          // Exponential backoff: 0s, 2s, 10s, 30s
          if (retryContext.previousRetryCount === 0) return 0
          if (retryContext.previousRetryCount === 1) return 2000
          if (retryContext.previousRetryCount === 2) return 10000
          return 30000
        },
      })
      .configureLogging(signalR.LogLevel.Information)
      .build()

    connectionRef.current = connection

    connection.on('DeviceStatusChanged', (data) => {
      console.log('SignalR: DeviceStatusChanged', data)
      onEvent?.('DeviceStatusChanged', data)
    })

    connection.on('DevicePing', (data) => {
      console.log('SignalR: DevicePing', data)
      onEvent?.('DevicePing', data)
    })

    connection.on('GrabberUpdate', (data) => {
      console.log('SignalR: GrabberUpdate', data)
      onEvent?.('GrabberUpdate', data)
    })

    connection.on('GrabCompleted', (data) => {
      console.log('SignalR: GrabCompleted', data)
      onEvent?.('GrabCompleted', data)
    })

    connection.on('DeviceTransferStarted', (data) => {
      console.log('SignalR: DeviceTransferStarted', data)
      onEvent?.('DeviceTransferStarted', data)
    })

    connection.on('DeviceTransferProgress', (data) => {
      onEvent?.('DeviceTransferProgress', data)
    })

    connection.on('DeviceGrabCompleted', (data) => {
      console.log('SignalR: DeviceGrabCompleted', data)
      onEvent?.('DeviceGrabCompleted', data)
    })

    connection.on('DeviceKnocking', (data) => {
      console.log('SignalR: DeviceKnocking', data)
      onEvent?.('DeviceKnocking', data)
    })

    connection.on('FlowLayoutChanged', (data) => {
      console.log('SignalR: FlowLayoutChanged', data)
      onEvent?.('FlowLayoutChanged', data)
    })

    connection.on('ConnectionsChanged', (data) => {
      console.log('SignalR: ConnectionsChanged', data)
      onEvent?.('ConnectionsChanged', data)
    })

    connection.on('DeployNotification', (data) => {
      console.log('SignalR: DeployNotification', data)
      onEvent?.('DeployNotification', data)
    })

    connection.onreconnecting((error) => {
      console.warn('SignalR: Reconnecting...', error)
      setConnectionState('Reconnecting')
    })

    connection.onreconnected((connectionId) => {
      console.log('SignalR: Reconnected', connectionId)
      setConnectionState('Connected')
    })

    connection.onclose((error) => {
      console.error('SignalR: Connection closed', error)
      setConnectionState('Disconnected')
    })

    const startConnection = async () => {
      try {
        await connection.start()
        console.log('SignalR: Connected', connection.connectionId)
        setConnectionState('Connected')
      } catch (err) {
        console.error('SignalR: Connection failed', err)
        setConnectionState('Failed')
      }
    }

    startConnection()

    return () => {
      console.log('SignalR: Disconnecting...')
      connection.stop()
    }
  }, [onEvent])

  return {
    connection: connectionRef.current,
    connectionState,
  }
}
