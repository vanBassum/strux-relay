import { useState } from "react"

import { AppSidebar } from "@/components/app/app-sidebar"
import { AppTopbar } from "@/components/app/app-topbar"
import { DevicesPage } from "@/components/app/devices-page"
import { DEFAULT_PAGE, type Page } from "@/components/app/navigation"
import { SidebarInset, SidebarProvider } from "@/components/ui/sidebar"
import { Toaster } from "@/components/ui/sonner"
import { TooltipProvider } from "@/components/ui/tooltip"
import { RelayProvider, useRelay, useRelayContext } from "@/hooks/use-relay"

/** Split from App so everything below it can reach the hub through the context. */
function Workspace() {
  const { state, session, reconnect } = useRelayContext()
  const [page, setPage] = useState<Page>(DEFAULT_PAGE)

  return (
    <>
      <AppSidebar active={page} session={session} onSelect={setPage} />
      {/* min-h-0 so the page gives up room to the bar pinned above it, rather
          than growing and pushing it off the top of the window. */}
      <SidebarInset className="min-h-0">
        <AppTopbar page={page} state={state} onRetry={reconnect} />
        <div className="flex min-h-0 flex-1 flex-col overflow-y-auto">
          <DevicesPage />
        </div>
      </SidebarInset>
    </>
  )
}

export function App() {
  const relay = useRelay()

  return (
    <RelayProvider value={relay}>
      <TooltipProvider>
        <SidebarProvider>
          <Workspace />
        </SidebarProvider>
      </TooltipProvider>
      <Toaster />
    </RelayProvider>
  )
}

export default App
