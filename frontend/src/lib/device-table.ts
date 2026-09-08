import type { Device } from "@/hooks/use-devices"

export type SortKey =
  | "name"
  | "project"
  | "connection"
  | "approval"
  | "lastSeen"
  | "address"
  | "firmware"

export type Sort = { key: SortKey; direction: "asc" | "desc" }

export type Filters = {
  query: string
  connection: "all" | "online" | "offline"
  approval: "all" | "pending" | "approved"
}

export const NO_FILTERS: Filters = {
  query: "",
  connection: "all",
  approval: "all",
}

export const CONNECTION_OPTIONS = [
  { value: "all" as const, label: "Any connection" },
  { value: "online" as const, label: "Online" },
  { value: "offline" as const, label: "Offline" },
]

export const APPROVAL_OPTIONS = [
  { value: "all" as const, label: "Any approval" },
  { value: "pending" as const, label: "Pending" },
  { value: "approved" as const, label: "Approved" },
]

// 25 by default: enough that a normal install is one page, small enough that the
// page is not a scroll to nowhere when somebody has a lot of boards.
export const PAGE_SIZES = [10, 25, 50, 100]
export const DEFAULT_PAGE_SIZE = 25

/**
 * What a column sorts on. Two of these are deliberately not alphabetical:
 * ascending Connection puts online first and ascending Approval puts pending
 * first, because "sort by approval" means "show me what needs a decision", not
 * "order these words". Sorting them as text would put approved above pending and
 * offline above online, which is backwards in both cases.
 */
function value(device: Device, key: SortKey): string | number | null {
  switch (key) {
    case "name":
      return (device.name || device.deviceId).toLowerCase()
    case "project":
      return device.project ? device.project.toLowerCase() : null
    case "connection":
      return device.connection === "online" ? 0 : 1
    case "approval":
      return device.approval === "pending" ? 0 : 1
    case "lastSeen":
      return device.lastSeen ? Date.parse(device.lastSeen) : null
    case "address":
      return device.address
    case "firmware":
      return device.firmware || null
  }
}

export function sortDevices(devices: Device[], sort: Sort | null): Device[] {
  if (!sort) return devices

  const ordered = [...devices].sort((left, right) => {
    const a = value(left, sort.key)
    const b = value(right, sort.key)

    // Nulls last in BOTH directions: a device with no address is not the
    // lowest-addressed device, it is one the question does not apply to, so
    // reversing the sort should not float it to the top.
    if (a === null && b === null) return 0
    if (a === null) return 1
    if (b === null) return -1

    const compared = typeof a === "number" ? a - (b as number) : a.localeCompare(b as string)
    return sort.direction === "asc" ? compared : -compared
  })

  return ordered
}

export function filterDevices(devices: Device[], filters: Filters): Device[] {
  const needle = filters.query.trim().toLowerCase()

  return devices.filter((device) => {
    if (filters.connection !== "all" && device.connection !== filters.connection)
      return false
    if (filters.approval !== "all" && device.approval !== filters.approval)
      return false
    if (!needle) return true

    return [device.name, device.deviceId, device.project, device.firmware].some(
      (field) => field.toLowerCase().includes(needle)
    )
  })
}
