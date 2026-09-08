import { useState } from "react"

import { AppSidebar } from "@/components/app/app-sidebar"
import { AppTopbar } from "@/components/app/app-topbar"
import { DevicePage } from "@/components/app/device-page"
import { DevicesPage } from "@/components/app/devices-page"
import { RELAY_HOME, type View } from "@/components/app/navigation"
import { SidebarInset, SidebarProvider } from "@/components/ui/sidebar"
import { Toaster } from "@/components/ui/sonner"
import { TooltipProvider } from "@/components/ui/tooltip"
import { useTheme } from "@/components/theme-provider"
import { useDeviceNavigation } from "@/hooks/use-device-navigation"
import { useDevices } from "@/hooks/use-devices"
import { RelayProvider, useRelay, useRelayContext } from "@/hooks/use-relay"
import { DEFAULT_DEVICE_PAGE } from "@/lib/device-nav"

/** Split from App so everything below it can reach the hub through the context. */
function Workspace() {
  const { state, session, reconnect } = useRelayContext()
  // Held here rather than in the page: the sidebar needs the selected device too,
  // and a second useDevices would mean a second copy of the same list.
  const devices = useDevices()

  // Two pieces of state, not one. Which device is in scope survives going back
  // to the list — the DEVICE section stays in the sidebar and pressing another
  // row is what moves it — so it cannot live inside the view.
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [view, setView] = useState<View>(RELAY_HOME)

  // Resolved from the live list every render, not stored alongside the id. The
  // list changes underneath us — the relay pushes when a device connects or is
  // forgotten — so a device forgotten while selected simply stops resolving and
  // the DEVICE section goes with it, no cleanup required.
  const selected =
    devices.devices.find((device) => device.deviceId === selectedId) ?? null

  const deviceNav = useDeviceNavigation(selected)

  // The stored page may not be one this device offers — it will not be, the day
  // these come from a manifest and two devices offer different pages — so it is
  // validated against the navigation rather than trusted.
  const requested = view.kind === "device" ? view.page : null
  const devicePage = selected
    ? (deviceNav.find((item) => item.id === requested)?.id ??
      (deviceNav[0]?.id ?? null))
    : null
  const activeItem = deviceNav.find((item) => item.id === devicePage) ?? null

  const openDevice = (deviceId: string) => {
    setSelectedId(deviceId)
    setView({ kind: "device", page: DEFAULT_DEVICE_PAGE })
  }

  const onDevice = view.kind === "device" && selected !== null && activeItem !== null

  return (
    <>
      <AppSidebar
        session={session}
        atRelayHome={view.kind === "devices"}
        onRelayHome={() => setView(RELAY_HOME)}
        device={selected}
        deviceNav={deviceNav}
        // Nothing is the active page while the list is showing, even though a
        // device is still selected: the highlight says where you are.
        devicePage={onDevice ? devicePage : null}
        onDevicePage={(page) => setView({ kind: "device", page })}
      />
      {/* min-h-0 so the page gives up room to the bar pinned above it, rather
          than growing and pushing it off the top of the window. */}
      <SidebarInset className="min-h-0">
        <AppTopbar
          state={state}
          onRetry={reconnect}
          trail={
            onDevice
              ? [
                  { label: "Devices", onClick: () => setView(RELAY_HOME) },
                  { label: selected.name || selected.deviceId },
                  { label: activeItem.label },
                ]
              : [{ label: "Devices" }]
          }
        />
        <div className="flex min-h-0 flex-1 flex-col overflow-y-auto">
          {onDevice ? (
            <DevicePage device={selected} item={activeItem} />
          ) : (
            <DevicesPage
              devices={devices}
              selectedId={selectedId}
              onOpen={openDevice}
            />
          )}
        </div>
      </SidebarInset>
    </>
  )
}

export function App() {
  const relay = useRelay()
  // sonner's wrapper reads next-themes, which this app does not use, so without
  // being told the theme its toasts follow the OS and ignore the toggle.
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
