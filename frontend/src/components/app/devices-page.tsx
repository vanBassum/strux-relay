import { useMemo, useState } from "react"
import { CpuIcon, SearchIcon } from "lucide-react"

import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import { DeviceUrl } from "@/components/app/device-url"
import { useDevices, type Device } from "@/hooks/use-devices"
import { absolute, ago } from "@/lib/time"

function ConnectionCell({ device }: { device: Device }) {
  const online = device.connection === "online"
  return (
    <span className="flex items-center gap-1.5">
      <span
        className={`size-1.5 rounded-full ${online ? "bg-emerald-500" : "bg-muted-foreground/40"}`}
      />
      {online ? "Online" : "Offline"}
    </span>
  )
}

function matches(device: Device, query: string): boolean {
  const needle = query.trim().toLowerCase()
  if (!needle) return true
  return [device.name, device.deviceId, device.project, device.firmware].some(
    (field) => field.toLowerCase().includes(needle)
  )
}

export function DevicesPage() {
  const { devices, loading, approve, forget } = useDevices()
  const [query, setQuery] = useState("")

  const shown = useMemo(
    () => devices.filter((device) => matches(device, query)),
    [devices, query]
  )

  const waiting = devices.filter((d) => d.approval === "pending").length

  return (
    <div className="flex flex-col gap-4 p-4">
      {/* No title or blurb: the bar above already says Devices, and the URL is
          self-explanatory on a page about pointing devices at this relay. */}
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="relative max-w-xs flex-1 basis-64">
          <SearchIcon className="text-muted-foreground pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2" />
          <Input
            className="pl-8"
            placeholder="Search devices…"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
          />
        </div>
        <DeviceUrl />
      </div>

      <div className="rounded-lg border">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Name</TableHead>
              <TableHead>Project</TableHead>
              <TableHead>Connection</TableHead>
              <TableHead>Approval</TableHead>
              <TableHead>Last seen</TableHead>
              <TableHead>Address</TableHead>
              <TableHead>Version</TableHead>
              <TableHead className="text-right">Actions</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {shown.length === 0 ? (
              <TableRow>
                <TableCell colSpan={8} className="text-muted-foreground py-8">
                  <span className="flex items-center justify-center gap-2">
                    <CpuIcon className="size-4" />
                    {loading
                      ? "Loading…"
                      : devices.length === 0
                        ? "No devices yet — point one at the URL above."
                        : "Nothing matches that search."}
                  </span>
                </TableCell>
              </TableRow>
            ) : (
              shown.map((device) => (
                // Keyed on the pair, not the id: two tokens claiming one id are
                // two rows, and that is deliberately visible.
                <TableRow key={`${device.deviceId}/${device.token ?? ""}`}>
                  <TableCell>
                    <div className="font-medium">
                      {device.name || device.deviceId}
                    </div>
                    <div className="text-muted-foreground font-mono text-xs">
                      {device.deviceId}
                    </div>
                  </TableCell>
                  <TableCell>{device.project || "—"}</TableCell>
                  <TableCell>
                    <ConnectionCell device={device} />
                  </TableCell>
                  <TableCell>
                    {device.approval === "pending" ? (
                      <Badge variant="outline">
                        Pending
                        {device.attempts && device.attempts > 1
                          ? ` · ${device.attempts} tries`
                          : ""}
                      </Badge>
                    ) : (
                      <Badge variant="secondary">Approved</Badge>
                    )}
                  </TableCell>
                  <TableCell title={absolute(device.lastSeen)}>
                    {ago(device.lastSeen)}
                  </TableCell>
                  <TableCell className="font-mono text-xs">
                    {device.address ?? "—"}
                  </TableCell>
                  <TableCell className="font-mono text-xs">
                    {device.firmware}
                  </TableCell>
                  <TableCell className="text-right">
                    <div className="flex justify-end gap-1">
                      {device.approval === "pending" && (
                        <Button size="sm" onClick={() => void approve(device)}>
                          Approve
                        </Button>
                      )}
                      <Button
                        size="sm"
                        variant="outline"
                        onClick={() => void forget(device)}
                      >
                        {device.approval === "pending" ? "Reject" : "Forget"}
                      </Button>
                    </div>
                  </TableCell>
                </TableRow>
              ))
            )}
          </TableBody>
        </Table>
      </div>

      {waiting > 0 && (
        <p className="text-muted-foreground text-xs">
          {waiting} waiting for a decision. Rejecting is not a block — a refused
          device keeps retrying, so it will reappear here.
        </p>
      )}
    </div>
  )
}
