import { useState, useEffect } from 'react'
import './SignalRDebug.css'

function SignalRDebug({ connectionState }) {
  const [events, setEvents] = useState([])
  const [isOpen, setIsOpen] = useState(false)

  useEffect(() => {
    const originalLog = console.log
    console.log = (...args) => {
      originalLog(...args)
      if (args[0]?.startsWith?.('SignalR:')) {
        setEvents((prev) => [
          {
            timestamp: new Date().toLocaleTimeString(),
            message: args.join(' '),
          },
          ...prev.slice(0, 49),
        ])
      }
    }
    return () => {
      console.log = originalLog
    }
  }, [])

  if (!isOpen) {
    return (
      <button className="signalr-debug-toggle" onClick={() => setIsOpen(true)}>
        🔍 SignalR Debug
      </button>
    )
  }

  return (
    <div className="signalr-debug">
      <div className="signalr-debug-header">
        <h3>SignalR Events</h3>
        <div className="signalr-debug-actions">
          <span className={`status-badge ${connectionState.toLowerCase()}`}>
            {connectionState}
          </span>
          <button onClick={() => setEvents([])}>Clear</button>
          <button onClick={() => setIsOpen(false)}>×</button>
        </div>
      </div>
      <div className="signalr-debug-events">
        {events.length === 0 ? (
          <div className="no-events">No events yet</div>
        ) : (
          events.map((event, index) => (
            <div key={index} className="event-item">
              <span className="event-time">{event.timestamp}</span>
              <span className="event-message">{event.message}</span>
            </div>
          ))
        )}
      </div>
    </div>
  )
}

export default SignalRDebug
