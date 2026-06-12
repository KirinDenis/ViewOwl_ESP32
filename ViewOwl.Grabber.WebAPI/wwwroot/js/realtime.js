/**
 * @file realtime.js
 * @description SignalR hub connection manager.
 *
 * Usage:
 *   await realtime.connect(jwtToken);    // call once after login
 *   realtime.on("GrabCompleted", fn);    // subscribe anywhere, even before connect()
 *   realtime.off("GrabCompleted", fn);   // unsubscribe
 *
 * Handlers registered before connect() are forwarded to the underlying
 * connection once it is established.  The connection retries automatically
 * with SignalR's built-in exponential back-off.
 */
const realtime = (() => {

  /** @type {signalR.HubConnection|null} */
  let _connection = null;

  /**
   * Pre-connect handler registry.
   * Maps event name → Set of callbacks so we can forward them on connect()
   * and correctly remove them via off().
   * @type {Object.<string, Set<Function>>}
   */
  const _pending = {};

  // ── Public API ─────────────────────────────────────────────────────────────

  /**
   * Establishes the SignalR connection to /hubs/viewowl.
   * Idempotent — calling it a second time is a no-op if already connected.
   * @param {string} token  JWT access token forwarded as ?access_token=.
   */
  async function connect(token) {
    if (_connection) return;

    _connection = new signalR.HubConnectionBuilder()
      .withUrl("/hubs/viewowl", { accessTokenFactory: () => token })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    // Forward all handlers that were registered before connect() was called
    for (const [event, callbacks] of Object.entries(_pending)) {
      for (const cb of callbacks) {
        _connection.on(event, cb);
      }
    }

    _connection.onreconnected(() => {
      console.info("[realtime] SignalR reconnected.");
    });

    _connection.onclose((err) => {
      if (err) console.warn("[realtime] SignalR connection closed with error:", err);
    });

    try {
      await _connection.start();
      console.info("[realtime] SignalR connected.");
    } catch (err) {
      // Non-fatal: the dashboard works without real-time updates;
      // pages that depend on events keep their polling fallbacks.
      console.warn("[realtime] SignalR initial connect failed:", err);
    }
  }

  /**
   * Subscribes to a named hub event.
   * Safe to call before connect() — the handler is queued and forwarded once
   * the connection is established.
   * @param {string}   event     Hub method name (e.g. "GrabCompleted").
   * @param {Function} callback  Invoked with the event payload when received.
   */
  function on(event, callback) {
    if (!_pending[event]) _pending[event] = new Set();
    _pending[event].add(callback);
    _connection?.on(event, callback);
  }

  /**
   * Unsubscribes a previously registered callback.
   * @param {string}   event
   * @param {Function} callback
   */
  function off(event, callback) {
    _pending[event]?.delete(callback);
    _connection?.off(event, callback);
  }

  return { connect, on, off };
})();
