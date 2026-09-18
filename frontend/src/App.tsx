import { AppSidebar } from "@/components/app/app-sidebar"
import { AppTopbar } from "@/components/app/app-topbar"
import { DevicesPage } from "@/components/app/devices-page"
import { RELAY_PAGES } from "@/components/app/navigation"
import { CachePage } from "@/components/app/cache-page"
import { McpPage } from "@/components/app/mcp-page"
import { TelemetryPage } from "@/components/app/telemetry-page"
import { SidebarInset, SidebarProvider } from "@/components/ui/sidebar"
import { Toaster } from "@/components/ui/sonner"
import { TooltipProvider } from "@/components/ui/tooltip"
import { useTheme } from "@/components/theme-provider"
import { useHashRoute } from "@/hooks/use-hash-route"
import { useDevices } from "@/hooks/use-devices"
import { RelayProvider, useRelay, useRelayContext } from "@/hooks/use-relay"

/** Split from App so everything below it can reach the hub through the context. */
function Workspace() {
  const { state, session, reconnect } = useRelayContext()
  const { page, navigate } = useHashRoute()
  // Held here rather than in the page so the list has one owner, and because the
  // relay pushes into it — a device connecting or being forgotten changes it
  // underneath whatever is showing.
  const devices = useDevices()

  // Three pages, all of them this relay's own. A DEVICE is not a page here: opening
  // one leaves this shell for the site the device itself serves, at /devices/<id>/.
  // That used to be a page — an in-shell device route that read the firmware's UI
  // manifest and composed its module bundles into this sidebar — and the cost was a
  // hop: you clicked a device, landed on a page about the device, and clicked again
  // to reach the device. One click is the whole point, so the hop is gone and the
  // module host with it.

  return (
    <>
      <AppSidebar
        session={session}
        relayPage={page}
        onRelayPage={navigate}
      />
      {/* min-h-0 so the page gives up room to the bar pinned above it, rather than
          growing and pushing it off the top of the window. */}
      <SidebarInset className="min-h-0">
        <AppTopbar
          state={state}
          onRetry={reconnect}
          trail={[
            {
              label:
                RELAY_PAGES.find((item) => item.id === page)?.label ?? "Devices",
            },
          ]}
        />
        <div className="flex min-h-0 flex-1 flex-col overflow-y-auto">
          {page === "telemetry" ? (
            <TelemetryPage />
          ) : page === "cache" ? (
            <CachePage />
          ) : page === "mcp" ? (
            <McpPage />
          ) : (
            <DevicesPage devices={devices} />
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
