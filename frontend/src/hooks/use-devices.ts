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

  return { devices, loading, approve, forget }
}

export type DeviceList = ReturnType<typeof useDevices>
