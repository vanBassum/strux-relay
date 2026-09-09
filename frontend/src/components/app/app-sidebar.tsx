import {
  DownloadIcon,
  HouseIcon,
  RadioTowerIcon,
  SettingsIcon,
  TerminalIcon,
} from "lucide-react"

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
import {
  sameDevicePage,
  type DevicePage,
  type DeviceShellPage,
} from "@/hooks/use-hash-route"

/// This shell's own per-device pages. Components here rather than icon names,
/// unlike the module nav: these are compiled in, so there is no wire format to
/// survive and no indirection to earn its keep.
const DEVICE_TOOLS: {
  page: DeviceShellPage
  label: string
  icon: typeof TerminalIcon
}[] = [
  { page: "console", label: "Console", icon: TerminalIcon },
  { page: "settings", label: "Settings", icon: SettingsIcon },
  { page: "firmware", label: "Firmware", icon: DownloadIcon },
]

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
  onDeviceOverview,
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
  /** Which device page is showing, or null for the overview. */
  devicePage: DevicePage | null
  onDevicePage: (page: DevicePage) => void
  /** The overview is this SHELL's page for a device, so it gets its own handler. */
  onDeviceOverview: () => void
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
                  {/* Overview belongs to this shell, not to the manifest — the same
                      way Devices and Cache do. It shows the device's contributed
                      CARDS, which is what most firmware declares and no page of its
                      own: a product's main feature belongs on the screen you land
                      on. So it is always first and always present, and the entries
                      below it are whatever the firmware added beyond that. */}
                  <SidebarMenuItem>
                    <SidebarMenuButton
                      isActive={devicePage === null}
                      tooltip="Overview"
                      onClick={onDeviceOverview}
                    >
                      <HouseIcon />
                      <span>Overview</span>
                    </SidebarMenuButton>
                  </SidebarMenuItem>

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

                {/* This shell's own pages for a device, below whatever the firmware
                    contributed. Below, because the order says what belongs to whom:
                    Overview and the module pages are this product; these two are the
                    framework's — every Strux device has `log list` and
                    `settings list`, which is exactly why they are not modules. */}
                <SidebarMenu>
                  {DEVICE_TOOLS.map(({ page, label, icon: Icon }) => (
                    <SidebarMenuItem key={page}>
                      <SidebarMenuButton
                        isActive={sameDevicePage(devicePage, { kind: "shell", page })}
                        tooltip={label}
                        onClick={() => onDevicePage({ kind: "shell", page })}
                      >
                        <Icon />
                        <span>{label}</span>
                      </SidebarMenuButton>
                    </SidebarMenuItem>
                  ))}
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
