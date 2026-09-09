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
import { HOME, useHashRoute } from "@/hooks/use-hash-route"
import { useDevices } from "@/hooks/use-devices"
import { RelayProvider, useRelay, useRelayContext } from "@/hooks/use-relay"
import { useDeviceModules } from "@/shell/module-host"

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

  // The requested page may not be one this device offers — with nav coming from a
  // manifest that is the normal case, not an edge one, because two devices offer
  // different pages and a bookmark outlives a reflash. So it is validated against the
  // manifest rather than trusted, and anything unrecognised falls back to the
  // overview, which is this shell's own page and always exists.
  const requested = route.kind === "device" ? route.page : null
  const devicePage = selected
    ? (modules.nav.find((item) => item.id === requested)?.id ?? null)
    : null
  const activeItem = modules.nav.find((item) => item.id === devicePage) ?? null

  // A page id in the URL that this device does not have gets corrected to the
  // overview. `replace`, not `navigate`: the user did not ask for this step, and it
  // would otherwise sit in the back stack redirecting forward again. Only once the
  // manifest has actually answered — before that "not found" only means "not yet".
  useEffect(() => {
    if (route.kind !== "device" || modules.status === "loading") return
    if (!route.page || devicePage === route.page) return
    replace({ kind: "device", deviceId: route.deviceId, page: null })
  }, [route, devicePage, modules.status, replace])

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
        onDeviceOverview={() =>
          selected &&
          navigate({ kind: "device", deviceId: selected.deviceId, page: null })
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
                  { label: activeItem ? activeItem.label : "Overview" },
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
              pageId={devicePage}
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
