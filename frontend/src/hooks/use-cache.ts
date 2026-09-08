import { useCallback, useEffect, useState } from "react"
import { toast } from "sonner"

import { useRelayContext } from "@/hooks/use-relay"

export type DeviceCacheStatus = "empty" | "ready" | "partial" | "error"

export type DeviceCache = {
  deviceId: string
  name: string
  online: boolean
  status: DeviceCacheStatus
  files: number
  bytes: number
  warmedFiles: number
  expectedFiles: number
  lastWarmedAt: string | null
  lastUsedAt: string | null
  lastError: string | null
}

export type CachePolicy = {
  immutableAssets: string
  mutableFiles: string
  serverLifetime: string
  maxBytes: number
  eviction: string
  warmOnConnect: boolean
}

export type CacheStats = {
  bytes: number
  maxBytes: number
  files: number
  devicesWithContent: number
  devicesKnown: number
  hits: number
  misses: number
  bytesServed: number
  bytesFetched: number
  failedFetches: number
}

export type CacheView = {
  stats: CacheStats
  policy: CachePolicy
  devices: DeviceCache[]
}

type ActionResult = { ok: boolean; error: string | null; affected: number }

/**
 * The relay's frontend cache.
 *
 * Read on demand rather than streamed: unlike telemetry these numbers only move
 * when somebody loads a device page or warms one, and the relay pushes
 * "CacheChanged" when an action here changes something. A device connecting also
 * drops its cache, which arrives as "DevicesChanged".
 */
export function useCache() {
  const { invoke, on, onConnected } = useRelayContext()
  const [cache, setCache] = useState<CacheView | null>(null)
  const [busy, setBusy] = useState<string | null>(null)

  const reload = useCallback(async () => {
    try {
      setCache(await invoke<CacheView>("GetCache"))
    } catch {
      /* the connection indicator already says it */
    }
  }, [invoke])

  useEffect(() => onConnected(() => void reload()), [onConnected, reload])
  useEffect(() => on("CacheChanged", () => void reload()), [on, reload])
  // A connect drops that device's cache, so the page is stale after one.
  useEffect(() => on("DevicesChanged", () => void reload()), [on, reload])

  /**
   * Runs one action, keeping which one is in flight so the page can disable just
   * that row's buttons. Warming takes a device's pipe several times over, so it
   * is not instant and a button that looked idle would invite a second click.
   */
  const run = useCallback(
    async (key: string, method: string, describe: (result: ActionResult) => string, deviceId?: string) => {
      setBusy(key)
      try {
        const result = await invoke<ActionResult>(
          method,
          ...(deviceId === undefined ? [] : [deviceId])
        )
        if (result.ok) toast.success(describe(result))
        else toast.error(result.error ?? "That did not work.")
      } catch {
        toast.error("Could not reach the relay.")
      } finally {
        setBusy(null)
      }
    },
    [invoke]
  )

  const warmDevice = useCallback(
    (device: DeviceCache) =>
      run(
        `warm:${device.deviceId}`,
        "WarmDeviceCache",
        (result) => `Warmed ${device.name} — ${result.affected} files.`,
        device.deviceId
      ),
    [run]
  )

  const clearDevice = useCallback(
    (device: DeviceCache) =>
      run(
        `clear:${device.deviceId}`,
        "ClearDeviceCache",
        (result) => `Cleared ${result.affected} files for ${device.name}.`,
        device.deviceId
      ),
    [run]
  )

  const warmAll = useCallback(
    () =>
      run("warm:all", "WarmAllCaches", (result) =>
        result.affected === 0
          ? "No connected devices to warm."
          : `Warmed ${result.affected} device${result.affected === 1 ? "" : "s"}.`
      ),
    [run]
  )

  const clearAll = useCallback(
    () =>
      run("clear:all", "ClearAllCaches", (result) =>
        `Cleared ${result.affected} cached file${result.affected === 1 ? "" : "s"}.`
      ),
    [run]
  )

  return { cache, busy, reload, warmDevice, clearDevice, warmAll, clearAll }
}
