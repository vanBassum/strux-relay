import { RadioTowerIcon } from "lucide-react"

import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarGroup,
  SidebarGroupContent,
  SidebarGroupLabel,
  SidebarHeader,
  SidebarMenu,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarTrigger,
} from "@/components/ui/sidebar"
import { PAGES, type Page } from "@/components/app/navigation"
import type { Session } from "@/hooks/use-relay"

export function AppSidebar({
  active,
  session,
  onSelect,
}: {
  active: Page
  session: Session | null
  onSelect: (page: Page) => void
}) {
  return (
    // "icon" rather than "offcanvas": collapsing narrows the rail to the icons
    // instead of taking the nav away, so the pages stay one click apart.
    <Sidebar collapsible="icon">
      <SidebarHeader>
        <div className="flex items-center gap-2">
          {/* Brand goes when collapsed and the trigger takes the rail's width,
              because both together do not fit in it. */}
          <RadioTowerIcon className="size-4 shrink-0 group-data-[collapsible=icon]:hidden" />
          <span className="font-medium group-data-[collapsible=icon]:hidden">
            Strux relay
          </span>
          <SidebarTrigger className="ml-auto group-data-[collapsible=icon]:ml-0" />
        </div>
      </SidebarHeader>

      <SidebarContent>
        <SidebarGroup>
          <SidebarGroupLabel>Relay</SidebarGroupLabel>
          <SidebarGroupContent>
            <SidebarMenu>
              {PAGES.map(({ page, icon: Icon }) => (
                <SidebarMenuItem key={page}>
                  {/* The tooltip is what names the page once the label is gone. */}
                  <SidebarMenuButton
                    isActive={page === active}
                    tooltip={page}
                    onClick={() => onSelect(page)}
                  >
                    <Icon />
                    <span>{page}</span>
                  </SidebarMenuButton>
                </SidebarMenuItem>
              ))}
            </SidebarMenu>
          </SidebarGroupContent>
        </SidebarGroup>
      </SidebarContent>

      <SidebarFooter>
        <div className="text-muted-foreground px-2 py-1 font-mono text-xs group-data-[collapsible=icon]:hidden">
          {session ? `v${session.health.version}` : "…"}
        </div>
      </SidebarFooter>
    </Sidebar>
  )
}
