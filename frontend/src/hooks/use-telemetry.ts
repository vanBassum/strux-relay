import { useCallback, useEffect, useRef, useState } from "react"

import { useRelayContext } from "@/hooks/use-relay"

export type SinkState = "notConfigured" | "ready" | "connected" | "error"

export type SinkDetail = { label: string; value: string }

export type SinkStatus = {
  name: string
  state: SinkState
  /** Non-secret configuration only — the token is reported as set/not set. */
  details: SinkDetail[]
  lastError: string | null
  lastErrorAt: string | null
  lastWriteAt: string | null
}

export type DropCounts = {
  noSinkConfigured: number
  invalidEvent: number
  queueFull: number
}

export type TelemetryCounters = {
  received: number
  forwarded: number
  dropped: number
  failed: number
  ratePerSecond: number
  lastEventAt: string | null
  lastForwardAt: string | null
  drops: DropCounts
}

export type TelemetryStatus = {
  sink: SinkStatus
  counters: TelemetryCounters
}

export type TelemetryEvent = {
  sequence: number
  at: string
  deviceId: string
  deviceName: string
  measurement: string
  /** The field set as the device wrote it: `temperature=21.5,unit="C"`. */
  payload: string
  tags: string
}

/**
 * The browser keeps the only list of recent events there is — the relay forwards
 * and forgets. Bounded, because this is a diagnostics view and an unbounded list
 * on a page left open for a day is a memory leak with a table in front of it.
 */
const MAX_EVENTS = 500

/**
 * Live telemetry, for as long as this page is open.
 *
 * Subscribing is explicit: the relay pushes to watchers only, so a dashboard on
 * the device list does not receive device-rate traffic. Nothing is replayed on
 * subscribe and nothing survives a refresh, which is a property of the design
 * rather than a gap — the relay is not a telemetry store.
 */
export function useTelemetry() {
  const { invoke, on, onConnected } = useRelayContext()

  const [status, setStatus] = useState<TelemetryStatus | null>(null)
  const [events, setEvents] = useState<TelemetryEvent[]>([])
  const [paused, setPaused] = useState(false)

  // Read inside a push handler that is registered once, so the flag cannot be
  // captured by value — a stale closure would keep appending after a pause. Synced
  // in an effect rather than assigned during render, which is not allowed.
  const pausedRef = useRef(paused)
  useEffect(() => {
    pausedRef.current = paused
  }, [paused])

  useEffect(
    () =>
      on<TelemetryEvent[]>("TelemetryEvents", (incoming) => {
        if (pausedRef.current) return
        setEvents((current) => {
          // Newest first, and the tail beyond the cap goes. Slicing here rather
          // than when rendering keeps the array itself bounded.
          const next = [...incoming].reverse().concat(current)
          return next.length > MAX_EVENTS ? next.slice(0, MAX_EVENTS) : next
        })
      }),
    [on]
  )

  useEffect(() => on<TelemetryStatus>("TelemetryStatus", setStatus), [on])

  // Re-subscribed on every connect, not just the first: a reconnect is a new
  // SignalR connection and its group membership does not survive.
  useEffect(
    () =>
      onConnected(() => {
        void invoke("SubscribeTelemetry")
        void invoke<TelemetryStatus>("GetTelemetry").then(setStatus).catch(() => {})
      }),
    [invoke, onConnected]
  )

  useEffect(
    () => () => {
      // Best effort: if the connection is already gone the group went with it.
      void invoke("UnsubscribeTelemetry").catch(() => {})
    },
    [invoke]
  )

  /** Clears what this browser is showing. The relay has nothing to clear. */
  const clear = useCallback(() => setEvents([]), [])

  return {
    status,
    events,
    paused,
    setPaused,
    clear,
    capacity: MAX_EVENTS,
  }
}
