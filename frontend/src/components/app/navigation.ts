import { HardDriveIcon, type LucideIcon } from "lucide-react"

export type Page = "Devices"

export const PAGES: { page: Page; icon: LucideIcon }[] = [
  { page: "Devices", icon: HardDriveIcon },
]

export const DEFAULT_PAGE: Page = "Devices"
