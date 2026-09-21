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

/**
 * Which devices the list is ABOUT, in the only two sizes anybody asked for: the
 * ones you can use right now, or every one the relay knows.
 *
 * This replaced a connection dropdown and an approval dropdown, which between them
 * offered nine combinations of two facts that are not independent — a pending
 * device is never online, so "online + not approved" was a filter that could only
 * ever return nothing. Neither answered the question somebody opens this page with,
 * which is "what can I talk to": offline, pending and half-connected are all the
 * same answer to that one, and it is not this device.
 */
export type Scope = "ready" | "all"

export type Filters = {
  query: string
  scope: Scope
}

// Ready by default: the page opens on what can be worked with. "All" is one click
// away and the counts on the control say how much it is holding back.
export const NO_FILTERS: Filters = {
  query: "",
  scope: "ready",
}

/**
 * Usable RIGHT NOW, and every clause is load-bearing:
 *
 * - approved, or the relay refuses it before the socket upgrade at all;
 * - online, or there is no pipe to carry a command;
 * - `ready`, which is the RELAY's own answer — `DeviceConnection.Ready`, the
 *   channels handshake having settled — reported per device so this shell does not
 *   invent a second definition of usable that can drift from the one the relay
 *   enforces. Online without it is a device still being shaken hands with: the
 *   relay cannot mint a channel id yet, so a command sent now goes nowhere.
 *
 * The first two are strictly implied by the third today and are still spelled out,
 * because they are what the word MEANS. A relay that one day reported `ready` for
 * something unapproved would be the bug, and this reads as the definition rather
 * than as a shortcut that happens to hold.
 */
export function isReady(device: Device): boolean {
  return (
    device.approval === "approved" &&
    device.connection === "online" &&
    device.ready
  )
}

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

/** Does the text match, ignoring which scope is selected. */
function matches(device: Device, needle: string): boolean {
  if (!needle) return true
  return [device.name, device.deviceId, device.project, device.firmware].some(
    (field) => field.toLowerCase().includes(needle)
  )
}

/**
 * Scope AND search, so searching inside "Ready" searches the ready devices — and
 * the counts below are counts of what each scope would show FOR THE CURRENT
 * SEARCH. Typing a name only an offline board has therefore reads "Ready 0 | All
 * 1", which is the answer, rather than an empty table that looks like a device
 * that no longer exists.
 */
export function filterDevices(devices: Device[], filters: Filters): Device[] {
  const needle = filters.query.trim().toLowerCase()

  return devices.filter(
    (device) =>
      (filters.scope === "all" || isReady(device)) && matches(device, needle)
  )
}

/** How many rows each scope would show for this search. */
export function scopeCounts(
  devices: Device[],
  query: string
): Record<Scope, number> {
  const needle = query.trim().toLowerCase()
  const found = devices.filter((device) => matches(device, needle))
  return { ready: found.filter(isReady).length, all: found.length }
}
