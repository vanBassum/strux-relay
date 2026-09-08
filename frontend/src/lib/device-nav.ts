import {
  ActivityIcon,
  FileTextIcon,
  HouseIcon,
  SettingsIcon,
  SquareTerminalIcon,
  type LucideIcon,
} from "lucide-react"

/**
 * Icons are referenced by NAME, not by component, and that is the whole point of
 * this indirection: a device manifest is JSON coming off the wire, so it can name
 * an icon but cannot hand over a React component. Resolving the name here means
 * the switch from a hardcoded array to a manifest changes nothing above it.
 *
 * An unknown name is not an error — a device may name an icon a newer shell
 * knows and this one does not — so it falls back rather than failing.
 */
export const DEVICE_NAV_ICONS: Record<string, LucideIcon> = {
  overview: HouseIcon,
  configuration: SettingsIcon,
  commands: SquareTerminalIcon,
  diagnostics: ActivityIcon,
  logs: FileTextIcon,
}

export const FALLBACK_NAV_ICON = FileTextIcon

export function navIcon(name: string): LucideIcon {
  return DEVICE_NAV_ICONS[name] ?? FALLBACK_NAV_ICON
}

/**
 * One page a device contributes to the sidebar.
 *
 * Deliberately shaped like a manifest entry rather than like a component: `id`
 * is what the shell routes on and what a module's registration will key against,
 * and everything here survives a round trip through JSON. Nothing in this type
 * refers to React.
 */
export type DeviceNavItem = {
  /** Stable across renames — the shell routes on this, not on the label. */
  id: string
  label: string
  /** A key into DEVICE_NAV_ICONS; unknown names fall back. */
  icon: string
}

/**
 * Placeholder navigation, standing in for what a device will declare in its
 * manifest. Static on purpose: the module system is not being built yet, and
 * this exists so the shell that will host it can be designed against real
 * entries instead of an empty list.
 */
export const PLACEHOLDER_DEVICE_NAV: DeviceNavItem[] = [
  { id: "overview", label: "Overview", icon: "overview" },
  { id: "configuration", label: "Configuration", icon: "configuration" },
  { id: "commands", label: "Commands", icon: "commands" },
  { id: "diagnostics", label: "Diagnostics", icon: "diagnostics" },
]

export const DEFAULT_DEVICE_PAGE = PLACEHOLDER_DEVICE_NAV[0].id
