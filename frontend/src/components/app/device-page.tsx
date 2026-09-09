import type { ReactNode } from "react"
import { ExternalLinkIcon } from "lucide-react"

import { Button } from "@/components/ui/button"
import type { Device } from "@/hooks/use-devices"
import { deviceUiUrl } from "@/lib/device-url"
import type { ManifestStatus } from "@/shell/module-registry"
import { ModulePageView, useDeviceCards } from "@/shell/module-host"
import type { DevicePage as DevicePageRoute } from "@/hooks/use-hash-route"
import { DeviceConsolePage } from "@/components/app/device-console-page"
import { DeviceSettingsPage } from "@/components/app/device-settings-page"
import { DeviceFirmwarePage } from "@/components/app/device-firmware-page"

/**
 * One device, in this shell.
 *
 * Three views, and which one you get is decided by evidence rather than by
 * configuration: the device either declares UI modules or it does not.
 *
 * `pageId` names a module PAGE, drawn by a bundle the FIRMWARE ships and imported over
 * that device's own pipe — this file knows nothing about what is in it.
 *
 * With no `pageId` this is the device's OVERVIEW: its module cards, which is the same
 * thing the device's own shell puts on its home screen. That is the common case, not a
 * fallback — a product's main feature belongs on the first screen you land on, so most
 * modules declare a card and no page at all.
 *
 * With neither, the device's own site is the page and the relay serves it whole over
 * the same pipe. That is not scaffolding waiting to be deleted: the fleet is mixed and
 * will stay mixed, so devices old enough, small enough or simply never rebuilt keep
 * working exactly as they did. All of it rides one transport underneath, which is why
 * this is a routing decision and not several designs.
 */
export function DevicePage({
  device,
  page,
  status,
  detail,
}: {
  device: Device
  /** A module page, one of this shell's own device pages, or null for the overview. */
  page: DevicePageRoute | null
  status: ManifestStatus
  detail: string
}) {
  if (page?.kind === "module")
    return <ModulePageView device={device} pageId={page.id} />

  if (page?.kind === "shell") {
    // Framework pages, not modules: every Strux device has `log list`,
    // `settings list` and `partition list`, and all three describe themselves.
    if (page.page === "console") return <DeviceConsolePage device={device} />
    if (page.page === "settings") return <DeviceSettingsPage device={device} />
    return <DeviceFirmwarePage device={device} />
  }

  return <DeviceOverview device={device} status={status} detail={detail} />
}

/// The device's own cards, or an honest account of why there are none.
function DeviceOverview({
  device,
  status,
  detail,
}: {
  device: Device
  status: ManifestStatus
  detail: string
}) {
  const cards = useDeviceCards(device)

  if (cards.length > 0)
    return (
      <div className="p-4">
        <div className="mx-auto max-w-2xl space-y-6">
          {cards.map((card) => (
            <div key={`${card.moduleId}/${card.id}`}>
              {card.render ? (
                (card.render() as ReactNode)
              ) : (
                <div className="bg-card text-card-foreground rounded-xl border p-6 text-sm shadow-sm">
                  <span className="font-mono">{card.moduleId}</span>{" "}
                  {card.failure
                    ? `could not be loaded: ${card.failure}`
                    : `declared a card "${card.id}" that its bundle did not register.`}
                </div>
              )}
            </div>
          ))}
        </div>
      </div>
    )

  return (
    <div className="flex flex-col items-start gap-3 p-4">
      <h2 className="text-sm font-medium">{headline(status)}</h2>
      <p className="text-muted-foreground max-w-prose text-sm">
        {detail ||
          "This device has not told the relay about any UI it can contribute."}
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
            // Base UI assumes a native <button> unless told otherwise, and this one is
            // a link: without it the primitive applies button semantics to an anchor.
            nativeButton={false}
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
      // A manifest that declares neither a card nor a page. Legal, and not worth an
      // apology: the firmware registered a UiModule and nothing in it.
      return "This device contributes no UI to the relay"
  }
}
