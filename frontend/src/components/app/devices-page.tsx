import { useMemo, useState } from "react"
import {
  ChevronDownIcon,
  ChevronLeftIcon,
  ChevronRightIcon,
  ChevronUpIcon,
  ChevronsUpDownIcon,
  CpuIcon,
  SearchIcon,
} from "lucide-react"

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
import { ChoiceFilter } from "@/components/app/device-filters"
import { DeviceUrl } from "@/components/app/device-url"
import { useDevices, type Device } from "@/hooks/use-devices"
import { useNow } from "@/hooks/use-now"
import {
  APPROVAL_OPTIONS,
  CONNECTION_OPTIONS,
  DEFAULT_PAGE_SIZE,
  NO_FILTERS,
  PAGE_SIZES,
  filterDevices,
  sortDevices,
  type Filters,
  type Sort,
  type SortKey,
} from "@/lib/device-table"
import { absolute, ago, duration } from "@/lib/time"

const COLUMNS: { key: SortKey; label: string }[] = [
  { key: "name", label: "Name" },
  { key: "project", label: "Project" },
  { key: "connection", label: "Connection" },
  { key: "approval", label: "Approval" },
  { key: "lastSeen", label: "Last seen" },
  { key: "address", label: "Address" },
  { key: "firmware", label: "Version" },
]

function SortableHead({
  column,
  sort,
  onSort,
}: {
  column: { key: SortKey; label: string }
  sort: Sort | null
  onSort: (key: SortKey) => void
}) {
  const active = sort?.key === column.key
  const Icon = !active
    ? ChevronsUpDownIcon
    : sort.direction === "asc"
      ? ChevronUpIcon
      : ChevronDownIcon

  return (
    <TableHead>
      <button
        type="button"
        className="flex items-center gap-1 hover:text-foreground"
        onClick={() => onSort(column.key)}
      >
        {column.label}
        <Icon
          className={`size-3.5 ${active ? "" : "text-muted-foreground/50"}`}
        />
      </button>
    </TableHead>
  )
}

export function DevicesPage() {
  const { devices, loading, approve, forget } = useDevices()

  // Relative times are rendered, not stored, so something has to re-render them.
  useNow()

  const [filters, setFilters] = useState<Filters>(NO_FILTERS)
  const [sort, setSort] = useState<Sort | null>(null)
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE)
  const [page, setPage] = useState(1)

  // No sort at all is the useful default, not a missing feature: the relay
  // returns pending first and then by name, which is the order somebody opening
  // this page wants. A click sorts on top of that.
  const matched = useMemo(
    () => sortDevices(filterDevices(devices, filters), sort),
    [devices, filters, sort]
  )

  const pages = Math.max(1, Math.ceil(matched.length / pageSize))

  // Clamped while rendering rather than corrected in an effect. The list shrinks
  // on its own — a device is forgotten, or the relay pushes a change — so the
  // stored page can go out of range without anybody touching the controls.
  // Storing the fix would mean a render showing an empty table first, and a
  // setState in an effect to repair it.
  const current = Math.min(page, pages)
  const start = (current - 1) * pageSize
  const shown = matched.slice(start, start + pageSize)

  const changeFilters = (change: Partial<Filters>) => {
    setFilters((previous) => ({ ...previous, ...change }))
    setPage(1)
  }

  /** First click sorts ascending, second flips it, third clears back to the relay's order. */
  const toggleSort = (key: SortKey) => {
    setSort((previous) => {
      if (previous?.key !== key) return { key, direction: "asc" }
      return previous.direction === "asc" ? { key, direction: "desc" } : null
    })
    setPage(1)
  }

  return (
    <div className="flex flex-col gap-4 p-4">
      {/* No title or blurb: the bar above already says Devices, and the URL is
          self-explanatory on a page about pointing devices at this relay. */}
      <div className="flex flex-wrap items-center gap-3">
        <div className="relative max-w-xs flex-1 basis-56">
          <SearchIcon className="text-muted-foreground pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2" />
          <Input
            className="pl-8"
            placeholder="Search devices…"
            value={filters.query}
            onChange={(event) => changeFilters({ query: event.target.value })}
          />
        </div>
        <ChoiceFilter
          label="Filter by connection"
          value={filters.connection}
          options={CONNECTION_OPTIONS}
          onChange={(connection) => changeFilters({ connection })}
        />
        <ChoiceFilter
          label="Filter by approval"
          value={filters.approval}
          options={APPROVAL_OPTIONS}
          onChange={(approval) => changeFilters({ approval })}
        />
        <div className="ml-auto">
          <DeviceUrl />
        </div>
      </div>

      <div className="rounded-lg border">
        <Table>
          <TableHeader>
            <TableRow>
              {COLUMNS.map((column) => (
                <SortableHead
                  key={column.key}
                  column={column}
                  sort={sort}
                  onSort={toggleSort}
                />
              ))}
              <TableHead className="text-right">Actions</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {shown.length === 0 ? (
              <TableRow>
                <TableCell
                  colSpan={COLUMNS.length + 1}
                  className="text-muted-foreground py-8"
                >
                  <span className="flex items-center justify-center gap-2">
                    <CpuIcon className="size-4" />
                    {loading
                      ? "Loading…"
                      : devices.length === 0
                        ? "No devices yet — point one at the URL above."
                        : "Nothing matches those filters."}
                  </span>
                </TableCell>
              </TableRow>
            ) : (
              shown.map((device) => (
                <DeviceRow
                  // Keyed on the pair, not the id: two tokens claiming one id are
                  // two rows, and that is deliberately visible.
                  key={`${device.deviceId}/${device.token ?? ""}`}
                  device={device}
                  onApprove={approve}
                  onForget={forget}
                />
              ))
            )}
          </TableBody>
        </Table>
      </div>

      <div className="text-muted-foreground flex flex-wrap items-center gap-3 text-xs">
        <span>
          {matched.length === 0
            ? "No devices"
            : `Showing ${start + 1}–${Math.min(start + pageSize, matched.length)} of ${matched.length}`}
          {matched.length !== devices.length && ` (of ${devices.length})`}
        </span>

        <div className="ml-auto flex items-center gap-2">
          <Button
            size="icon"
            variant="outline"
            aria-label="Previous page"
            disabled={current <= 1}
            onClick={() => setPage(current - 1)}
          >
            <ChevronLeftIcon />
          </Button>
          <span>
            {current} / {pages}
          </span>
          <Button
            size="icon"
            variant="outline"
            aria-label="Next page"
            disabled={current >= pages}
            onClick={() => setPage(current + 1)}
          >
            <ChevronRightIcon />
          </Button>
          <ChoiceFilter
            label="Devices per page"
            value={String(pageSize)}
            options={PAGE_SIZES.map((size) => ({
              value: String(size),
              label: `${size} per page`,
            }))}
            onChange={(size) => {
              setPageSize(Number(size))
              setPage(1)
            }}
          />
        </div>
      </div>
    </div>
  )
}

function DeviceRow({
  device,
  onApprove,
  onForget,
}: {
  device: Device
  onApprove: (device: Device) => Promise<void>
  onForget: (device: Device) => Promise<void>
}) {
  const online = device.connection === "online"

  return (
    <TableRow>
      <TableCell>
        <div className="font-medium">{device.name || device.deviceId}</div>
        <div className="text-muted-foreground font-mono text-xs">
          {device.deviceId}
        </div>
      </TableCell>
      <TableCell>{device.project || "—"}</TableCell>
      <TableCell>
        <span className="flex items-center gap-1.5">
          <span
            className={`size-1.5 rounded-full ${online ? "bg-emerald-500" : "bg-muted-foreground/40"}`}
          />
          {online ? "Online" : "Offline"}
        </span>
        {/* How long the pipe has been up, which is a different question from
            when the device last spoke — a board can hold one open for hours
            while saying nothing. */}
        {online && (
          <div
            className="text-muted-foreground text-xs"
            title={`Connected since ${absolute(device.connectedAt)}`}
          >
            for {duration(device.connectedAt)}
          </div>
        )}
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
      {/* "now" for a connected device, and that is a claim the transport
          actually supports: the firmware pings every 30 s and tears the pipe
          down when one fails, so an open pipe means it was alive within 30 s.
          Showing the last MESSAGE here instead read "6 minutes ago" beside a
          green Online dot — true, since an idle board says nothing for minutes,
          and alarming for no reason. The quiet-since detail moves to the title,
          where it informs rather than worries. */}
      <TableCell
        title={
          online
            ? `Last message ${ago(device.lastMessageAt)} · connected since ${absolute(device.connectedAt)}`
            : absolute(device.lastSeen)
        }
      >
        {online ? "now" : ago(device.lastSeen)}
      </TableCell>
      <TableCell className="font-mono text-xs">
        {device.address ?? "—"}
      </TableCell>
      <TableCell className="font-mono text-xs">{device.firmware}</TableCell>
      <TableCell className="text-right">
        <div className="flex justify-end gap-1">
          {device.approval === "pending" && (
            <Button size="sm" onClick={() => void onApprove(device)}>
              Approve
            </Button>
          )}
          <Button
            size="sm"
            variant="outline"
            onClick={() => void onForget(device)}
          >
            {device.approval === "pending" ? "Reject" : "Forget"}
          </Button>
        </div>
      </TableCell>
    </TableRow>
  )
}
