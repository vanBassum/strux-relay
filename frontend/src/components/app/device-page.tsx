import { ExternalLinkIcon } from "lucide-react"

import { Button } from "@/components/ui/button"
import type { Device } from "@/hooks/use-devices"
import type { DeviceNavItem } from "@/lib/device-nav"
import { deviceUiUrl } from "@/lib/device-url"

/**
 * Where a device-provided page will render.
 *
 * Deliberately almost empty. The point of this step is the shell — the sidebar,
 * the device scope, and the navigation between them — and filling this with
 * plausible-looking status cards would mean inventing readings the relay cannot
 * actually get yet, which is worse than an honest blank.
 *
 * What it does offer is the way out: no device contributes modules yet, and in a
 * mixed fleet some never will, so the device's own site is the real page for this
 * device and saying so here beats an apology with no next step.
 */
export function DevicePage({
  device,
  item,
}: {
  device: Device
  item: DeviceNavItem
}) {
  const online = device.connection === "online"

  return (
    <div className="flex flex-col items-start gap-3 p-4">
      <h2 className="text-sm font-medium">{item.label}</h2>
      <p className="text-muted-foreground max-w-prose text-sm">
        A placeholder. This is where{" "}
        <span className="font-medium">{device.name || device.deviceId}</span>{" "}
        will render its own <span className="font-mono text-xs">{item.id}</span>{" "}
        page, served from the device's own build rather than from this relay.
      </p>
      <p className="text-muted-foreground max-w-prose text-sm">
        Until it ships modules, its whole UI is one page the relay serves over the
        same pipe.
      </p>
      {online ? (
        <Button
          variant="outline"
          size="sm"
          render={
            <a
              href={deviceUiUrl(device.deviceId)}
              target="_blank"
              rel="noreferrer"
            />
          }
        >
          <ExternalLinkIcon />
          Open device UI
        </Button>
      ) : (
        <>
          <Button variant="outline" size="sm" disabled>
            <ExternalLinkIcon />
            Open device UI
          </Button>
          <p className="text-muted-foreground text-xs">
            This device is offline, so there is no pipe to load its page over.
          </p>
        </>
      )}
    </div>
  )
}
