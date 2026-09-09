import {
  ActivityIcon,
  BatteryIcon,
  BellIcon,
  CameraIcon,
  ClockIcon,
  CpuIcon,
  DownloadIcon,
  DropletIcon,
  FanIcon,
  GaugeIcon,
  HardDriveIcon,
  HomeIcon,
  LightbulbIcon,
  LockIcon,
  MoonIcon,
  PlugIcon,
  PowerIcon,
  PuzzleIcon,
  RadioIcon,
  SettingsIcon,
  SignalIcon,
  SlidersHorizontalIcon,
  SunIcon,
  TerminalIcon,
  ThermometerIcon,
  ToggleLeftIcon,
  Volume2Icon,
  WavesIcon,
  WifiIcon,
  WindIcon,
  ZapIcon,
  type LucideIcon,
} from "lucide-react"

/**
 * One page a device contributes to the sidebar.
 *
 * Shaped like a manifest entry rather than like a component, because that is now
 * literally what it is: `id` is what the shell routes on and what a module's
 * `routes.register` keys against, and everything here survived a round trip through
 * JSON. Nothing in this type refers to React.
 */
export type DeviceNavItem = {
  /** Stable across renames — the shell routes on this, not on the label. */
  id: string
  label: string
  /** A lucide icon name; unknown ones fall back rather than throwing. */
  icon: string
  /** Which module declared it, so a failure can name the bundle at fault. */
  moduleId: string
}

/**
 * Icons arrive as NAMES, because firmware cannot ship a React component — and the
 * two shells resolve those names against their own lucide versions. Strux is on
 * lucide ^0.577 and this shell on ^1.42, so a name valid on one may not exist on the
 * other, which is exactly why `resolveIcon` must never throw and never return
 * undefined. A missing icon is cosmetic; an exception here takes out the sidebar.
 *
 * A curated set rather than all of lucide: `import { icons } from "lucide-react"`
 * defeats tree shaking and pulls ~1500 icons — measured upstream at 491 KB — and
 * `dynamicIconImports` trades that for ~1500 chunks. A named import of a bounded set
 * costs nothing either way, and unknown names already have to fall back.
 *
 * Kept in step with Strux's own `src/shell/icons.ts` by hand. Divergence is not a
 * fault: a name this shell lacks renders the fallback, which is the designed
 * behaviour and the reason the contract writes it down.
 */
const TABLE: Record<string, LucideIcon> = {
  activity: ActivityIcon,
  battery: BatteryIcon,
  bell: BellIcon,
  camera: CameraIcon,
  clock: ClockIcon,
  cpu: CpuIcon,
  download: DownloadIcon,
  droplet: DropletIcon,
  fan: FanIcon,
  gauge: GaugeIcon,
  "hard-drive": HardDriveIcon,
  home: HomeIcon,
  lightbulb: LightbulbIcon,
  lock: LockIcon,
  moon: MoonIcon,
  plug: PlugIcon,
  power: PowerIcon,
  puzzle: PuzzleIcon,
  radio: RadioIcon,
  settings: SettingsIcon,
  signal: SignalIcon,
  "sliders-horizontal": SlidersHorizontalIcon,
  sun: SunIcon,
  terminal: TerminalIcon,
  thermometer: ThermometerIcon,
  "toggle-left": ToggleLeftIcon,
  "volume-2": Volume2Icon,
  waves: WavesIcon,
  wifi: WifiIcon,
  wind: WindIcon,
  zap: ZapIcon,
}

export const FALLBACK_NAV_ICON = PuzzleIcon

/**
 * Never throws, never returns undefined. Accepts the kebab-case spelling a manifest
 * will use, and tolerates the PascalCase and "…Icon" spellings so a name copied out
 * of a component import still resolves.
 */
export function navIcon(name: string | undefined): LucideIcon {
  if (!name) return FALLBACK_NAV_ICON

  const kebab = name
    .replace(/Icon$/, "")
    .replace(/([a-z0-9])([A-Z])/g, "$1-$2")
    .replace(/[_\s]+/g, "-")
    .toLowerCase()

  return TABLE[kebab] ?? TABLE[name.toLowerCase()] ?? FALLBACK_NAV_ICON
}
