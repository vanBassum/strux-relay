import { ChartColumnIcon, HardDriveIcon, type LucideIcon } from "lucide-react"

export type RelayPage = "devices" | "telemetry"

/**
 * Navigation the relay owns.
 *
 * Icons are components here, unlike the device navigation where they are names:
 * these pages are compiled into the shell, so there is no wire format to survive
 * and no indirection to earn its keep. The two sections being shaped differently
 * is the point — one is the shell's own, the other is contributed.
 */
export const RELAY_PAGES: { id: RelayPage; label: string; icon: LucideIcon }[] = [
  { id: "devices", label: "Devices", icon: HardDriveIcon },
  { id: "telemetry", label: "Telemetry", icon: ChartColumnIcon },
]

/**
 * What the main area is showing. Note what is NOT in here: which device is
 * selected.
 *
 * Selection outlives the view on purpose. Going back to the list is not
 * deselecting — the device stays in the sidebar so its pages are one click away,
 * and pressing another row is what changes it. Folding the id into the view
 * would have made "show the list" and "forget which device I was on" the same
 * action, which is why they were the same action before.
 */
export type View =
  | { kind: "relay"; page: RelayPage }
  | { kind: "device"; page: string }

export const RELAY_HOME: View = { kind: "relay", page: "devices" }
