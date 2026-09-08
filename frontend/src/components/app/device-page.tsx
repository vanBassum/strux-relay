import type { Device } from "@/hooks/use-devices"
import type { DeviceNavItem } from "@/lib/device-nav"

/**
 * Where a device-provided page will render.
 *
 * Deliberately almost empty. The point of this step is the shell — the sidebar,
 * the device scope, and the navigation between them — and filling this with
 * plausible-looking status cards would mean inventing readings the relay cannot
 * actually get yet, which is worse than an honest blank.
 */
export function DevicePage({
  device,
  item,
}: {
  device: Device
  item: DeviceNavItem
}) {
  return (
    <div className="flex flex-col gap-2 p-4">
      <h2 className="text-sm font-medium">{item.label}</h2>
      <p className="text-muted-foreground max-w-prose text-sm">
        A placeholder. This is where{" "}
        <span className="font-medium">{device.name || device.deviceId}</span>{" "}
        will render its own <span className="font-mono text-xs">{item.id}</span>{" "}
        page, served from the device's own build rather than from this relay.
      </p>
      {device.connection === "offline" && (
        <p className="text-muted-foreground text-xs">
          This device is offline, so there would be no pipe to load a page over.
        </p>
      )}
    </div>
  )
}
