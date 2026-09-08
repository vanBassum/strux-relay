import { CpuIcon } from "lucide-react"

import type { Device } from "@/hooks/use-devices"

/**
 * Which device the DEVICE section below is about.
 *
 * A header rather than a menu item, and the difference is deliberate: everything
 * under it is scoped to this device, so it has to read as the thing setting that
 * scope and not as another destination. Hence the surface and border, and hence
 * not being clickable — there is nowhere for it to go.
 *
 * It is the natural home for a device switcher later; the shape already leaves
 * room for one, but a chevron that opened nothing would promise something this
 * step does not do.
 */
export function DeviceContext({ device }: { device: Device }) {
  const online = device.connection === "online"

  return (
    <div className="bg-sidebar-accent/50 mx-2 flex items-center gap-2 rounded-md border p-2 group-data-[collapsible=icon]:hidden">
      <CpuIcon className="text-muted-foreground size-4 shrink-0" />
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-1.5">
          <span
            className={`size-1.5 shrink-0 rounded-full ${online ? "bg-emerald-500" : "bg-muted-foreground/40"}`}
            title={online ? "Online" : "Offline"}
          />
          <span className="truncate text-sm font-medium">
            {device.name || device.deviceId}
          </span>
        </div>
        <div className="text-muted-foreground truncate font-mono text-xs">
          {device.deviceId}
        </div>
      </div>
    </div>
  )
}
