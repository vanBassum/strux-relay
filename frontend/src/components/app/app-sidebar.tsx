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
  SidebarSeparator,
  SidebarTrigger,
} from "@/components/ui/sidebar"
import { DeviceContext } from "@/components/app/device-context"
import { RELAY_PAGES, type RelayPage } from "@/components/app/navigation"
import type { Device } from "@/hooks/use-devices"
import type { Session } from "@/hooks/use-relay"
import { navIcon, type DeviceNavItem } from "@/lib/device-nav"
import type { ManifestStatus } from "@/shell/module-registry"
import { sameDevicePage, type DevicePage } from "@/hooks/use-hash-route"

export function AppSidebar({
  session,
  relayPage,
  onRelayPage,
  device,
  deviceNav,
  navStatus,
  navDetail,
  devicePage,
  onDevicePage,
}: {
  session: Session | null
  /** Which relay page is showing, or null while a device page is. */
  relayPage: RelayPage | null
  onRelayPage: (page: RelayPage) => void
  /** The device in scope, or null when the device list is showing. */
  device: Device | null
  /** Read off this device's manifest — firmware decides what is in here, not a build. */
  deviceNav: DeviceNavItem[]
  /** Why the list above is empty, when it is. */
  navStatus: ManifestStatus
  navDetail: string
  /** Which device page is showing, or null while the manifest has not answered. */
  devicePage: DevicePage | null
  onDevicePage: (page: DevicePage) => void
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
        {/* Navigation the relay owns. Devices stays here whatever is selected, so
            there is always a way back to the list. */}
        <SidebarGroup>
          <SidebarGroupLabel>Relay</SidebarGroupLabel>
          <SidebarGroupContent>
            <SidebarMenu>
              {RELAY_PAGES.map(({ id, label, icon: Icon }) => (
                <SidebarMenuItem key={id}>
                  <SidebarMenuButton
                    isActive={id === relayPage}
                    tooltip={label}
                    onClick={() => onRelayPage(id)}
                  >
                    <Icon />
                    <span>{label}</span>
                  </SidebarMenuButton>
                </SidebarMenuItem>
              ))}
            </SidebarMenu>
          </SidebarGroupContent>
        </SidebarGroup>

        {/* Everything below belongs to one device, and the separator plus its own
            labelled group is what says so. Absent entirely when no device is in
            scope, rather than present and empty — an empty DEVICE heading would
            imply a device that contributes nothing. */}
        {device && (
          <>
            <SidebarSeparator />
            <SidebarGroup>
              <SidebarGroupLabel>Device</SidebarGroupLabel>
              <SidebarGroupContent className="flex flex-col gap-2">
                <DeviceContext device={device} />
                <SidebarMenu>
                  {/* Everything here comes from the manifest. There is no
                      Overview, no Console, no Settings and no Firmware that this
                      build put there — a shell contributes nothing to a device's
                      navigation, so this list is exactly what the firmware declared
                      and in the order it declared it. */}
                  {deviceNav.map((item) => {
                    const Icon = navIcon(item.icon)
                    return (
                      <SidebarMenuItem key={item.id}>
                        <SidebarMenuButton
                          isActive={sameDevicePage(devicePage, {
                            kind: "module",
                            id: item.id,
                          })}
                          tooltip={item.label}
                          onClick={() => onDevicePage({ kind: "module", id: item.id })}
                        >
                          <Icon />
                          <span>{item.label}</span>
                        </SidebarMenuButton>
                      </SidebarMenuItem>
                    )
                  })}
                </SidebarMenu>

                {/* Why the list above is empty, when it is — and only for the one
                    status that is worth a word here. "No modules" needs no notice:
                    Overview says it in full, and captioning the ordinary case
                    would make most of a mixed fleet look deficient. A version
                    mismatch is different: it is actionable, and without saying so
                    a device that has pages looks like a device that has none. */}
                {navStatus === "unsupported" && (
                  <p className="text-muted-foreground px-2 text-xs group-data-[collapsible=icon]:hidden">
                    {navDetail}
                  </p>
                )}

                {/* No "Open device UI" here any more, deliberately. This
                    section is navigation WITHIN the selected device — Overview
                    and whatever pages its firmware contributes — and the
                    device's own site is neither: it leaves this shell. Having
                    it here also made the same action reachable from two places
                    at once, and it was clicked by accident from both. It lives
                    in the row's kebab menu on the Devices page now, and on a
                    module-less device's Overview, which is the one place it is
                    the answer rather than an aside. */}
              </SidebarGroupContent>
            </SidebarGroup>
          </>
        )}
      </SidebarContent>

      <SidebarFooter>
        <div className="text-muted-foreground px-2 py-1 font-mono text-xs group-data-[collapsible=icon]:hidden">
          {session ? `v${session.health.version}` : "…"}
        </div>
      </SidebarFooter>
    </Sidebar>
  )
}
