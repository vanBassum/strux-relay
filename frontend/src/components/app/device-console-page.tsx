import { useCallback, useEffect, useRef, useState } from "react"
import { RefreshCwIcon } from "lucide-react"

import { Button } from "@/components/ui/button"
import { Skeleton } from "@/components/ui/skeleton"
import { Switch } from "@/components/ui/switch"
import type { Device } from "@/hooks/use-devices"
import { useRelayContext } from "@/hooks/use-relay"
import { deviceTransport } from "@/shell/device-transport"

/**
 * A device's log, read through the relay.
 *
 * **Polled, and that is a real limitation rather than a shortcut.** The device
 * broadcasts log lines continuously on session 0, but the relay fans those out to
 * *browser pipes* — the sockets a device's own page opens at `/devices/<id>/ws` — and
 * this shell is not one of those; it talks to the hub. So a live tail needs the relay
 * to also push session-0 chunks to a per-device hub group, which is a change in
 * `DeviceConnection` and not in this file.
 *
 * What `log list` gives instead is the device's own ring buffer, complete, on demand.
 * For reading why something happened that is usually the better artefact anyway — it
 * reaches back before the page was opened, which a tail never does. What it cannot do
 * is show a line the moment it is written, so the auto-refresh below is honest about
 * being a poll and is off by default: each one takes the device's single in-flight
 * pipe, so a console left open on a fast interval competes with everything else.
 */
interface LogReply {
  lines?: string[]
}

const INTERVAL_MS = 5000

/// Colour by ESP-IDF level, which is the first character of the line: `E (123) tag:`.
/// Anything unrecognised is left alone rather than guessed at.
function levelClass(line: string): string | undefined {
  const match = /^([EWIDV]) \(/.exec(line)
  switch (match?.[1]) {
    case "E":
      return "text-destructive"
    case "W":
      return "text-amber-600 dark:text-amber-500"
    case "D":
    case "V":
      return "text-muted-foreground"
    default:
      return undefined
  }
}

export function DeviceConsolePage({ device }: { device: Device }) {
  const { invoke } = useRelayContext()
  const transport = deviceTransport(invoke, device.deviceId)

  const [lines, setLines] = useState<string[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [auto, setAuto] = useState(false)
  const scroller = useRef<HTMLDivElement | null>(null)

  const load = useCallback(async () => {
    setBusy(true)
    try {
      const reply = await transport.request<LogReply>("log list")
      setLines(reply?.lines ?? [])
      setError(null)
    } catch (e) {
      // The previous contents stay on screen: a dropped poll is not news, and
      // blanking the log every time one fails would be worse than being stale.
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }, [transport])

  useEffect(() => {
    void load()
  }, [load])

  useEffect(() => {
    if (!auto) return
    const id = setInterval(() => void load(), INTERVAL_MS)
    return () => clearInterval(id)
  }, [auto, load])

  // Newest lines are at the bottom, so land there — but only when following, or a
  // refresh would yank the view away from whatever was being read.
  useEffect(() => {
    if (!auto || !scroller.current) return
    scroller.current.scrollTop = scroller.current.scrollHeight
  }, [lines, auto])

  if (device.connection !== "online")
    return (
      <div className="p-4">
        <div className="bg-card text-card-foreground max-w-2xl rounded-xl border p-6 text-sm shadow-sm">
          This device is offline, so there is no pipe to read its log over.
        </div>
      </div>
    )

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-3 p-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 className="text-lg font-semibold">Console</h2>
          <p className="text-muted-foreground text-sm">
            The device's own log buffer, via{" "}
            <code className="font-mono text-xs">log list</code>. Polled, not live —
            session&nbsp;0 broadcasts do not reach this shell yet.
          </p>
        </div>
        <div className="flex shrink-0 items-center gap-3">
          <label className="flex items-center gap-2 text-sm">
            <Switch checked={auto} onCheckedChange={setAuto} />
            Auto-refresh
          </label>
          <Button size="sm" variant="outline" disabled={busy} onClick={() => void load()}>
            <RefreshCwIcon />
            {busy ? "Reading…" : "Refresh"}
          </Button>
        </div>
      </div>

      {error && (
        <p className="text-destructive text-sm">
          Last read failed: {error}
          {lines ? " — showing the previous contents." : ""}
        </p>
      )}

      {!lines ? (
        <Skeleton className="min-h-0 flex-1" />
      ) : lines.length === 0 ? (
        <p className="text-muted-foreground text-sm">The log buffer is empty.</p>
      ) : (
        <div
          ref={scroller}
          className="bg-card min-h-0 flex-1 overflow-auto rounded-xl border p-3"
        >
          <pre className="font-mono text-xs leading-relaxed">
            {lines.map((line, i) => (
              <div key={i} className={levelClass(line)}>
                {line}
              </div>
            ))}
          </pre>
        </div>
      )}
    </div>
  )
}
