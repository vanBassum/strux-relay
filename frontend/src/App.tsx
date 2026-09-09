import { useEffect, useState } from "react"

import { AppSidebar } from "@/components/app/app-sidebar"
import { AppTopbar } from "@/components/app/app-topbar"
import { DevicePage } from "@/components/app/device-page"
import { DevicesPage } from "@/components/app/devices-page"
import { RELAY_PAGES } from "@/components/app/navigation"
import { CachePage } from "@/components/app/cache-page"
import { TelemetryPage } from "@/components/app/telemetry-page"
import { SidebarInset, SidebarProvider } from "@/components/ui/sidebar"
import { Toaster } from "@/components/ui/sonner"
import { TooltipProvider } from "@/components/ui/tooltip"
import { useTheme } from "@/components/theme-provider"
import {
  HOME,
  sameDevicePage,
  useHashRoute,
  type DevicePage as DevicePageRoute,
} from "@/hooks/use-hash-route"
import { useDevices } from "@/hooks/use-devices"
import { RelayProvider, useRelay, useRelayContext } from "@/hooks/use-relay"
import { useDeviceModules, useLandingPage } from "@/shell/module-host"

/** Split from App so everything below it can reach the hub through the context. */
function Workspace() {
  const { state, session, reconnect } = useRelayContext()
  const { route, navigate, replace } = useHashRoute()
  // Held here rather than in the page: the sidebar needs the selected device too,
  // and a second useDevices would mean a second copy of the same list.
  const devices = useDevices()

  // Which device is in scope, and it deliberately OUTLIVES the route. Going back to
  // the list is not deselecting — the DEVICE section stays in the sidebar so its
  // pages are one click away, and opening another row is what moves it. So the route
  // seeds this and never clears it.
  const [selectedId, setSelectedId] = useState<string | null>(
    route.kind === "device" ? route.deviceId : null,
  )

  useEffect(() => {
    if (route.kind === "device") setSelectedId(route.deviceId)
  }, [route])

  // Resolved from the live list every render, not stored alongside the id. The list
  // changes underneath us — the relay pushes when a device connects or is forgotten —
  // so a device forgotten while selected simply stops resolving and the DEVICE
  // section goes with it, no cleanup required.
  const selected =
    devices.devices.find((device) => device.deviceId === selectedId) ?? null

  // The manifest read. Per device, and re-read rather than remembered when a device
  // reconnects: it may have been reflashed while it was away.
  const modules = useDeviceModules(selected)

  // A MODULE page in the URL may not be one this device offers — with nav coming from
  // a manifest that is the normal case, not an edge one, because two devices offer
  // different pages and a bookmark outlives a reflash. So a module id is validated
  // against the manifest; this shell's own pages need no validation, since they exist
  // for every device whatever its firmware says.
  const requested = route.kind === "device" ? route.page : null
  const devicePage: DevicePageRoute | null = !selected
    ? null
    : requested?.kind === "module"
      ? (modules.nav.some((item) => item.id === requested.id) ? requested : null)
      : (requested ?? null)

  const activeItem =
    devicePage?.kind === "module"
      ? (modules.nav.find((item) => item.id === devicePage.id) ?? null)
      : null

  // Where to be when no page is named, or when the one named is not a page this
  // device has: the FIRST page its manifest declares. There is no overview to fall
  // back to any more — every page is the firmware's — so the firmware's own
  // declaration order is what decides, and this shell picks nothing.
  //
  // `replace`, not `navigate`: the user did not ask for this step, and it would
  // otherwise sit in the back stack redirecting forward again. Only once the manifest
  // has actually answered — before that, "not a page" only means "not yet".
  const landing = useLandingPage(selected)

  useEffect(() => {
    if (route.kind !== "device" || modules.status === "loading") return
    if (route.page && sameDevicePage(route.page, devicePage)) return
    if (!landing) return
    replace({
      kind: "device",
      deviceId: route.deviceId,
      page: { kind: "module", id: landing },
    })
  }, [route, devicePage, landing, modules.status, replace])

  // A device route whose device the relay has never heard of. Only once the list has
  // actually loaded — before that, "not found" just means "not yet".
  useEffect(() => {
    if (route.kind !== "device" || devices.loading) return
    if (devices.devices.some((device) => device.deviceId === route.deviceId)) return
    navigate(HOME)
  }, [route, devices.loading, devices.devices, navigate])

  const onDevice = route.kind === "device" && selected !== null
  const relayPage = route.kind === "relay" ? route.page : "devices"

  return (
    <>
      <AppSidebar
        session={session}
        relayPage={onDevice ? null : relayPage}
        onRelayPage={(page) => navigate({ kind: "relay", page })}
        device={selected}
        deviceNav={modules.nav}
        navStatus={modules.status}
        navDetail={modules.detail}
        // Nothing is the active page while the list is showing, even though a device
        // is still selected: the highlight says where you are.
        devicePage={onDevice ? devicePage : null}
        onDevicePage={(page) =>
          selected && navigate({ kind: "device", deviceId: selected.deviceId, page })
        }
      />
      {/* min-h-0 so the page gives up room to the bar pinned above it, rather than
          growing and pushing it off the top of the window. */}
      <SidebarInset className="min-h-0">
        <AppTopbar
          state={state}
          onRetry={reconnect}
          trail={
            onDevice
              ? [
                  { label: "Devices", onClick: () => navigate(HOME) },
                  {
                    label: selected.name || selected.deviceId,
                    onClick: () =>
                      navigate({
                        kind: "device",
                        deviceId: selected.deviceId,
                        page: null,
                      }),
                  },
                  { label: crumbFor(devicePage, activeItem?.label) },
                ]
              : [
                  {
                    label:
                      RELAY_PAGES.find((page) => page.id === relayPage)?.label ??
                      "Devices",
                  },
                ]
          }
        />
        <div className="flex min-h-0 flex-1 flex-col overflow-y-auto">
          {onDevice ? (
            <DevicePage
              device={selected}
              page={devicePage}
              status={modules.status}
              detail={modules.detail}
            />
          ) : relayPage === "telemetry" ? (
            <TelemetryPage />
          ) : relayPage === "cache" ? (
            <CachePage />
          ) : (
            <DevicesPage
              devices={devices}
              selectedId={selectedId}
              onOpen={(deviceId) => navigate({ kind: "device", deviceId, page: null })}
            />
          )}
        </div>
      </SidebarInset>
    </>
  )
}

/// What the breadcrumb calls the current device page. The manifest's own title when
/// the module is known, its id when the nav has not arrived yet, and "…" when there is
/// no page at all — never a name this build invented.
function crumbFor(page: DevicePageRoute | null, moduleLabel?: string): string {
  if (!page) return "…"
  return moduleLabel ?? page.id
}

export function App() {
  const relay = useRelay()
  // sonner's wrapper reads next-themes, which this app does not use, so without being
  // told the theme its toasts follow the OS and ignore the toggle.
  const { resolvedTheme } = useTheme()

  return (
    <RelayProvider value={relay}>
      <TooltipProvider>
        <SidebarProvider>
          <Workspace />
        </SidebarProvider>
      </TooltipProvider>
      <Toaster theme={resolvedTheme} />
    </RelayProvider>
  )
}

export default App
