import type { Device } from "@/hooks/use-devices"

/**
 * One per column, and there are five because each column now carries TWO facts:
 * Device is a name over an id, Firmware a project over a version, Status a
 * connection over how long it has been that way, Approval a decision over when it
 * was made. So a sort key is a key VECTOR, not a value — see `value` below.
 */
export type SortKey =
  | "device"
  | "firmware"
  | "status"
  | "approval"
  | "address"

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
  { value: "pending" as const, label: "Not approved" },
  { value: "approved" as const, label: "Approved" },
]

// 25 by default: enough that a normal install is one page, small enough that the
// page is not a scroll to nowhere when somebody has a lot of boards.
export const PAGE_SIZES = [10, 25, 50, 100]
export const DEFAULT_PAGE_SIZE = 25

/**
 * What a column sorts on: a vector compared element by element, because a column
 * showing two facts has to order by both. Ascending Status puts online first and
 * ascending Approval puts what needs a decision first — "sort by approval" means
 * "show me what is waiting", not "order these words".
 *
 * Both tie-breaks negate a timestamp, so equal-ranked rows come out MOST RECENT
 * first. That is deliberate and it is the same reason in both places: the offline
 * device seen an hour ago and the device approved yesterday are the ones somebody
 * is looking for, and burying them under a board last seen in June would be a
 * literal reading of "ascending" that nobody wants.
 */
function value(device: Device, key: SortKey): (string | number | null)[] {
  switch (key) {
    case "device":
      return [(device.name || device.deviceId).toLowerCase(), device.deviceId]
    case "firmware":
      return [
        device.project ? device.project.toLowerCase() : null,
        device.firmware || null,
      ]
    case "status":
      return [
        device.connection === "online" ? 0 : 1,
        device.lastSeen ? -Date.parse(device.lastSeen) : null,
      ]
    case "approval":
      return [
        device.approval === "pending" ? 0 : 1,
        device.approvedAt ? -Date.parse(device.approvedAt) : null,
      ]
    case "address":
      return [device.address]
  }
}

/// Nulls last in BOTH directions: a device with no address is not the
/// lowest-addressed device, it is one the question does not apply to, so reversing
/// the sort should not float it to the top.
function compare(a: string | number | null, b: string | number | null): number {
  if (a === null && b === null) return 0
  if (a === null) return 1
  if (b === null) return -1
  return typeof a === "number" ? a - (b as number) : a.localeCompare(b as string)
}

export function sortDevices(devices: Device[], sort: Sort | null): Device[] {
  if (!sort) return devices

  return [...devices].sort((left, right) => {
    const a = value(left, sort.key)
    const b = value(right, sort.key)

    for (let index = 0; index < a.length; index++) {
      const compared = compare(a[index], b[index])
      // The direction flips every element, including the tie-break: a reversed
      // column that kept its second key ascending would order rows by something
      // the header does not describe.
      if (compared !== 0) return sort.direction === "asc" ? compared : -compared
    }
    return 0
  })
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
