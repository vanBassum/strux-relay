import { ExternalLinkIcon } from "lucide-react"

import { Button } from "@/components/ui/button"
import type { Device } from "@/hooks/use-devices"
import { deviceUiUrl } from "@/lib/device-url"
import type { ManifestStatus } from "@/shell/module-registry"
import { ModulePageView } from "@/shell/module-host"

/**
 * One device, in this shell.
 *
 * Two outcomes, and which one you get is decided by evidence rather than by
 * configuration: the device either declares UI modules or it does not.
 *
 * With modules, the page is drawn by a bundle the FIRMWARE ships — imported over that
 * device's own pipe — and this file knows nothing about what is in it.
 *
 * Without them, the device's own site is the page, and the relay serves it whole over
 * the same pipe. That is not scaffolding waiting to be deleted: the fleet is mixed and
 * will stay mixed, so devices old enough, small enough or simply never rebuilt keep
 * working exactly as they did. Both modes ride one transport underneath, which is why
 * this is a routing decision and not two designs.
 */
export function DevicePage({
  device,
  pageId,
  status,
  detail,
}: {
  device: Device
  /** Null when this device declares no pages — then the fallback below is the page. */
  pageId: string | null
  status: ManifestStatus
  detail: string
}) {
  if (pageId) return <ModulePageView device={device} pageId={pageId} />

  return (
    <div className="flex flex-col items-start gap-3 p-4">
      <h2 className="text-sm font-medium">{headline(status)}</h2>
      <p className="text-muted-foreground max-w-prose text-sm">
        {detail ||
          "This device has not told the relay about any UI pages it can contribute."}
      </p>

      {status === "offline" ? (
        <>
          <Button variant="outline" size="sm" disabled>
            <ExternalLinkIcon />
            Open device UI
          </Button>
          <p className="text-muted-foreground text-xs">
            There is no pipe to load its page over either.
          </p>
        </>
      ) : (
        <>
          <p className="text-muted-foreground max-w-prose text-sm">
            Its whole UI is one page, which the relay serves over the same pipe.
          </p>
          <Button
            variant="outline"
            size="sm"
            render={
              <a href={deviceUiUrl(device.deviceId)} target="_blank" rel="noreferrer" />
            }
          >
            <ExternalLinkIcon />
            Open device UI
          </Button>
        </>
      )}
    </div>
  )
}

/// Deliberately different words per status. "No modules" is the ordinary answer for
/// most of a mixed fleet and must not read as a fault; "needs a newer relay" is
/// actionable and says which side to act on; an error is the only one that is a
/// problem, and saying so is the whole reason the relay classifies these rather than
/// collapsing them into "nothing came back".
function headline(status: ManifestStatus): string {
  switch (status) {
    case "loading":
      return "Reading this device's UI manifest…"
    case "absent":
      return "This device ships no UI modules"
    case "unsupported":
      return "This device's UI needs a newer relay"
    case "offline":
      return "This device is offline"
    case "error":
      return "Could not read this device's UI manifest"
    case "ready":
      // Ready with no pages: a device whose modules contribute cards only. This shell
      // has no per-device dashboard to put them on, so its own page is where to see
      // them — which is a gap worth naming rather than an error to report.
      return "This device contributes no pages to the relay"
  }
}
