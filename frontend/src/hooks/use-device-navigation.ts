import type { Device } from "@/hooks/use-devices"
import { PLACEHOLDER_DEVICE_NAV, type DeviceNavItem } from "@/lib/device-nav"

/**
 * The pages a device contributes to the sidebar.
 *
 * **This function is the swap point.** Today it returns a static array. When a
 * device serves a manifest, this becomes a read over that device's pipe — keyed
 * on `device.deviceId`, with loading and failure states of its own — and nothing
 * above it changes: the sidebar already treats the result as data that arrives
 * per device rather than as a constant it can import.
 *
 * Which is also why it takes the whole device rather than an id: a manifest read
 * needs to know whether there is a pipe to read it over, and a device that is
 * offline will contribute nothing.
 */
export function useDeviceNavigation(device: Device | null): DeviceNavItem[] {
  if (!device) return []

  // An offline device would have no manifest to serve, but the placeholders are
  // shown regardless — this step is about the shell's layout, and an empty
  // sidebar would not exercise it.
  return PLACEHOLDER_DEVICE_NAV
}
