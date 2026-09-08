import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react"
import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
} from "@microsoft/signalr"

export type ConnectionState =
  | "connecting"
  | "connected"
  | "reconnecting"
  | "disconnected"

export type Health = { status: string; version: string }
export type Session = { health: Health }

/**
 * The relay's hub, and the only way this app talks to the server — there is no
 * REST surface. Calls go out with `invoke`; the relay pushes "PairingChanged"
 * when the lists move, which is what replaces polling.
 */
export function useRelay() {
  const [state, setState] = useState<ConnectionState>("connecting")
  const [session, setSession] = useState<Session | null>(null)

  const connectionRef = useRef<HubConnection | null>(null)
  const connectedListeners = useRef(new Set<() => void>())
  // Push handlers live outside the connection: the hooks below this one
  // subscribe from their own effects, which run before this one builds it.
  const pushHandlers = useRef(new Map<string, Set<(payload: never) => void>>())
  const attached = useRef(new Set<string>())

  const announceConnected = useCallback(() => {
    connectedListeners.current.forEach((listener) => listener())
  }, [])

  /**
   * Run something every time the hub connects, including right now if it is
   * already connected. A reconnect is an event from outside React rather than a
   * render, so a refetch belongs here and not in an effect watching `state` —
   * but a page opened later would otherwise wait forever for the next connect,
   * so the last one is replayed to a late subscriber.
   */
  const onConnected = useCallback((listener: () => void) => {
    const listeners = connectedListeners.current
    listeners.add(listener)

    if (connectionRef.current?.state === HubConnectionState.Connected)
      queueMicrotask(() => {
        if (listeners.has(listener)) listener()
      })

    return () => {
      listeners.delete(listener)
    }
  }, [])

  /** Point one hub event at the handlers registered for it. Idempotent. */
  const attach = useCallback((connection: HubConnection, event: string) => {
    if (attached.current.has(event)) return
    attached.current.add(event)
    connection.on(event, (payload: never) => {
      pushHandlers.current.get(event)?.forEach((handler) => handler(payload))
    })
  }, [])

  /** Listen for something the relay pushes. Returns the unsubscribe. */
  const on = useCallback(
    <T>(event: string, handler: (payload: T) => void) => {
      const registry = pushHandlers.current
      let handlers = registry.get(event)
      if (!handlers) {
        handlers = new Set()
        registry.set(event, handlers)
      }
      handlers.add(handler as (payload: never) => void)

      const connection = connectionRef.current
      if (connection) attach(connection, event)

      return () => {
        handlers?.delete(handler as (payload: never) => void)
      }
    },
    [attach]
  )

  /**
   * Call a hub method. Rejects when there is no connection, so a caller can
   * tell "the relay is down" from "the call failed". The identity is stable, so
   * it is safe in a dependency list.
   */
  const invoke = useCallback(
    async <T>(method: string, ...args: unknown[]): Promise<T> => {
      const connection = connectionRef.current
      if (!connection || connection.state !== HubConnectionState.Connected)
        throw new Error("Not connected to the relay.")
      return connection.invoke<T>(method, ...args)
    },
    []
  )

  const reconnect = useCallback(() => {
    const connection = connectionRef.current
    if (!connection) return
    setState("reconnecting")
    void connection
      .start()
      .then(async () => {
        setState("connected")
        announceConnected()
        setSession(await connection.invoke<Session>("GetSession"))
      })
      .catch(() => setState("disconnected"))
  }, [announceConnected])

  useEffect(() => {
    let cancelled = false

    const connection = new HubConnectionBuilder()
      .withUrl("/hub")
      .withAutomaticReconnect()
      .build()

    connectionRef.current = connection

    // A fresh connection carries none of the old handlers, so re-wire whatever
    // subscribed before it existed.
    attached.current.clear()
    for (const event of pushHandlers.current.keys()) attach(connection, event)

    const loadSession = async () => {
      try {
        const loaded = await connection.invoke<Session>("GetSession")
        if (!cancelled) setSession(loaded)
      } catch {
        /* the banner already says the connection is gone */
      }
    }

    connection.onreconnecting(() => !cancelled && setState("reconnecting"))
    connection.onreconnected(() => {
      if (cancelled) return
      setState("connected")
      announceConnected()
      void loadSession()
    })
    connection.onclose(() => !cancelled && setState("disconnected"))

    const started = connection
      .start()
      .then(() => {
        if (cancelled) return
        setState("connected")
        announceConnected()
        void loadSession()
      })
      .catch(() => !cancelled && setState("disconnected"))

    return () => {
      cancelled = true
      connectionRef.current = null
      // Stopped only once the start has settled. StrictMode mounts, unmounts and
      // remounts, so an immediate stop lands in the middle of negotiation and
      // fails with "the connection was stopped during negotiation" — noise on
      // every single load, which is exactly what hides a real transport problem.
      void started.finally(() => connection.stop())
    }
  }, [attach, announceConnected])

  return useMemo(
    () => ({ state, session, invoke, on, onConnected, reconnect }),
    [state, session, invoke, on, onConnected, reconnect]
  )
}

export type Relay = ReturnType<typeof useRelay>

/**
 * One connection for the whole app: every page reads it, and a hook opening its
 * own would mean a second session on the relay.
 */
const RelayContext = createContext<Relay | null>(null)

export const RelayProvider = RelayContext.Provider

export function useRelayContext(): Relay {
  const relay = useContext(RelayContext)
  if (!relay) throw new Error("useRelayContext needs a RelayProvider above it.")
  return relay
}
