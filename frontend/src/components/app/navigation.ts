import {
  ChartColumnIcon,
  DatabaseIcon,
  HardDriveIcon,
  type LucideIcon,
} from "lucide-react"

export type RelayPage = "devices" | "telemetry" | "cache"

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
  { id: "cache", label: "Cache", icon: DatabaseIcon },
]

// Note what is NOT here any more: a `View` union, and a device page's default id.
// Where the dashboard is now lives in the URL — see hooks/use-hash-route.ts — because
// a device page became a real destination the moment its nav came from a manifest: it
// has to survive a refresh, be linkable, and answer the back button. And there is no
// default device page to name, because this build no longer knows what pages a device
// has; the first one the manifest declares is the one that opens.
