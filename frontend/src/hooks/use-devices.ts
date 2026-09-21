import { useCallback, useEffect, useState } from "react"
import { toast } from "sonner"

import { useRelayContext } from "@/hooks/use-relay"

export type Connection = "online" | "offline"
export type Approval = "pending" | "approved"

export type Device = {
  deviceId: string
  name: string
  project: string
  firmware: string
  connection: Connection
  approval: Approval
  /** The last time the device actually said something, not when this was fetched. */
  lastSeen: string | null
  address: string | null
  /** Live pipes only: when it came up, and when it last spoke. */
  connectedAt: string | null
  lastMessageAt: string | null
  approvedAt: string | null
  /** Only on a pending device: approving is keyed on the (id, token) pair. */
  token: string | null
  attempts: number | null
  /**
   * The git commit the firmware was built from, when it reports one. A version alone
   * does not identify a build — two boards can both say 0.0.6 and be different code.
   */
  commit: string | null
  /**
   * Everything else the device said about itself on connect. Open by design: the
   * device chooses the keys, the relay stores them all, and this shell shows the ones
   * it has a place for and keeps the rest readable rather than inventing cells for
   * facts it has never heard of.
   */
  details: Record<string, string> | null
  /** The one-line description the firmware reports about itself, when it reports one. */
  description: string | null
  /**
   * Whether an AI agent talking to this relay over MCP can see this device and run
   * its commands. Relay state, not something the device said — approving a device
   * lets a person drive it, this lets a model.
   */
  mcpExposed: boolean
  /**
   * Whether the relay can send this device a command right now. NOT the same as
   * `connection === "online"`: the socket is open for a moment before the channels
   * handshake settles, and until it does the relay cannot open a channel, so
   * nothing can be asked of the device. The relay computes it — this shell does not
   * get to have its own opinion about what "usable" means.
   */
  ready: boolean
}

/**
 * One list of devices, approved or waiting, connected or not. Loaded per connect
 * and re-read when the relay says something moved — the pairing store pushes
 * "PairingChanged" and the registry pushes "DevicesChanged". Nothing is polled.
 */
export function useDevices() {
  const { invoke, on, onConnected } = useRelayContext()
  const [devices, setDevices] = useState<Device[]>([])
  const [loading, setLoading] = useState(true)

  const reload = useCallback(async () => {
    try {
      setDevices(await invoke<Device[]>("GetDevices"))
    } catch {
      /* the connection indicator is already saying it */
    } finally {
      setLoading(false)
    }
  }, [invoke])

  useEffect(() => onConnected(() => void reload()), [onConnected, reload])
  useEffect(() => on("PairingChanged", () => void reload()), [on, reload])
  useEffect(() => on("DevicesChanged", () => void reload()), [on, reload])

  const approve = useCallback(
    async (device: Device) => {
      if (!device.token) return
      try {
        const result = await invoke<{ ok: boolean; error: string | null }>(
          "Approve",
          device.deviceId,
          device.token
        )
        if (!result.ok) {
          toast.error(result.error ?? "Could not approve that device.")
          return
        }
        // Nothing is sent to the device — it is already retrying, so its next
        // attempt is the one that succeeds.
        toast.success(`${device.name || device.deviceId} approved.`)
      } catch {
        toast.error("Could not reach the relay.")
      }
    },
    [invoke]
  )

  const forget = useCallback(
    async (device: Device) => {
      try {
        await invoke<{ ok: boolean; wasApproved: boolean }>(
          "Forget",
          device.deviceId
        )
        toast.success(
          device.approval === "pending"
            ? `${device.name || device.deviceId} rejected.`
            : `${device.name || device.deviceId} forgotten.`
        )
      } catch {
        toast.error("Could not reach the relay.")
      }
    },
    [invoke]
  )

  const setMcpExposure = useCallback(
    async (device: Device, exposed: boolean) => {
      try {
        const result = await invoke<{
          ok: boolean
          exposed: boolean
          error: string | null
        }>("SetMcpExposure", device.deviceId, exposed)
        if (!result.ok) {
          toast.error(result.error ?? "Could not change MCP exposure.")
          return
        }
        toast.success(
          exposed
            ? `${device.name || device.deviceId} is now reachable over MCP.`
            : `${device.name || device.deviceId} is no longer reachable over MCP.`
        )
      } catch {
        toast.error("Could not reach the relay.")
      }
    },
    [invoke]
  )

  return { devices, loading, approve, forget, setMcpExposure }
}

export type DeviceList = ReturnType<typeof useDevices>
