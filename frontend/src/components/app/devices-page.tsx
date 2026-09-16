import { useMemo, useState } from "react"
import {
  ChevronDownIcon,
  ChevronLeftIcon,
  ChevronRightIcon,
  ChevronUpIcon,
  CheckIcon,
  ChevronsUpDownIcon,
  CpuIcon,
  EllipsisIcon,
  ExternalLinkIcon,
  SearchIcon,
  Trash2Icon,
} from "lucide-react"

import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu"
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
import { deviceUiUrl } from "@/lib/device-url"
import type { Device, DeviceList } from "@/hooks/use-devices"
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
import { absolute, ago, date, duration } from "@/lib/time"

// Five columns carrying eight facts. Name and id are one thing to look at, and so
// are project and version, and so are "online" and how long it has been online — so
// each pair is one column with the identifying line on top and the qualifying one
// under it in a smaller, quieter type. Seven columns of one fact each said no more
// and made the table a horizontal scroll on anything but a wide window.
const COLUMNS: { key: SortKey; label: string }[] = [
  { key: "device", label: "Device" },
  { key: "firmware", label: "Firmware" },
  { key: "status", label: "Status" },
  { key: "approval", label: "Approval" },
  { key: "address", label: "Address" },
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

export function DevicesPage({
  devices: list,
}: {
  /** Passed in rather than fetched here: App owns the one copy of it. */
  devices: DeviceList
}) {
  const { devices, loading, approve, forget } = list

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
  const href = deviceUiUrl(device.deviceId)

  // Clicking a row opens the device's own site, and both conditions are load-bearing:
  // a pending device has no pipe, and an offline one has no pipe to fetch its page
  // over. Neither clicks through to anything, so neither row is clickable — the kebab
  // says why in words.
  const openable = online && device.approval === "approved"

  // A new tab, not this one. The list is where you came from and where you will want
  // to be again — with several devices in a fleet, opening one should not cost you
  // your place in the list, your filters or your sort.
  const open = () => window.open(href, "_blank", "noopener")

  return (
    <TableRow
      className={openable ? "cursor-pointer" : undefined}
      // A <tr> is not a button, so the keyboard handling has to be spelled out
      // rather than inherited: without this the whole list becomes unreachable
      // for anyone not using a mouse.
      role={openable ? "button" : undefined}
      tabIndex={openable ? 0 : undefined}
      aria-label={
        openable ? `Open ${device.name || device.deviceId}` : undefined
      }
      onClick={openable ? open : undefined}
      onKeyDown={
        openable
          ? (event) => {
              if (event.key === "Enter" || event.key === " ") {
                event.preventDefault()
                open()
              }
            }
          : undefined
      }
    >
      <TableCell>
        {/* A real anchor, even though the whole row already opens it. The row's
            click handler is not a link: it cannot be middle-clicked, copied, or
            opened by a browser's own "open in new window". The name is the thing
            somebody aims at anyway, so that is where the link goes.
            stopPropagation because otherwise the anchor AND the row both fire and
            you get two tabs. */}
        {openable ? (
          <a
            className="font-medium hover:underline"
            href={href}
            target="_blank"
            rel="noreferrer"
            onClick={(event) => event.stopPropagation()}
          >
            {device.name || device.deviceId}
          </a>
        ) : (
          <div className="font-medium">{device.name || device.deviceId}</div>
        )}
        <div className="text-muted-foreground font-mono text-xs">
          {device.deviceId}
        </div>
      </TableCell>

      {/* What it runs: which product, and which build of it. The version is mono
          because it is compared character by character — "0.0.6" against "0.0.16"
          is a reading somebody does with their eyes. */}
      <TableCell>
        <div>{device.project || "—"}</div>
        <div className="text-muted-foreground font-mono text-xs">
          {device.firmware}
        </div>
      </TableCell>

      <TableCell>
        <span className="flex items-center gap-1.5">
          <span
            className={`size-1.5 rounded-full ${online ? "bg-emerald-500" : "bg-muted-foreground/40"}`}
          />
          {online ? "Online" : "Offline"}
        </span>
        {/* Online says how long the PIPE has been up, which is a different question
            from when the device last spoke — a board can hold one open for hours
            while saying nothing. Offline has no pipe to measure, so it answers the
            other question instead. Showing the last MESSAGE while online read
            "6 minutes ago" beside a green dot: true, since an idle board says
            nothing for minutes, and alarming for no reason. It moves to the title,
            where it informs rather than worries. */}
        <div
          className="text-muted-foreground text-xs"
          title={
            online
              ? `Last message ${ago(device.lastMessageAt)} · connected since ${absolute(device.connectedAt)}`
              : absolute(device.lastSeen)
          }
        >
          {online
            ? `for ${duration(device.connectedAt)}`
            : device.lastSeen
              ? `last seen ${ago(device.lastSeen)}`
              : "never seen"}
        </div>
      </TableCell>

      {/* A DATE for an approved device, not another badge saying "Approved". The
          column already answers the yes/no question by whether it holds a date at
          all, so spending a badge on it would leave the two states looking equally
          weighty — and only one of them is asking for anything. Amber, and the only
          colour in the row besides the status dot, because a device waiting to be
          let in is the one thing on this page that wants doing. */}
      <TableCell>
        {device.approval === "pending" ? (
          <Badge
            variant="outline"
            className="border-amber-500/30 bg-amber-500/15 text-amber-700 dark:text-amber-400"
            title={
              device.attempts && device.attempts > 1
                ? `${device.attempts} connection attempts so far`
                : undefined
            }
          >
            Not approved
          </Badge>
        ) : (
          <span title={absolute(device.approvedAt)}>{date(device.approvedAt)}</span>
        )}
      </TableCell>

      <TableCell className="font-mono text-xs">
        {device.address ?? "—"}
      </TableCell>

      <TableCell className="text-right">
        {/* Stops here rather than on each button: approving or forgetting a
            device must not also open it, and putting the guard on the container
            means the next action added is covered without remembering to. */}
        <div
          className="flex justify-end gap-1"
          onClick={(event) => event.stopPropagation()}
        >
          {/* Every action is behind the kebab, Approve included. It was a button
              sitting in this cell, and the argument for that was real — approving is
              why somebody is looking at a pending row at all. What settles it is
              that the row now says "Not approved" in amber two columns to the left,
              which is a louder signal than the button was, and which does not put a
              primary button on every row of a fresh install.

              Opening the device is the ROW's click, so that entry is not how you get
              there either — it is where the answer lives when you cannot. A row that
              does nothing when clicked owes an explanation, and the disabled item
              with its title is it. */}
          <DropdownMenu>
            <DropdownMenuTrigger
              render={
                <Button
                  size="icon-sm"
                  variant="ghost"
                  aria-label={`Actions for ${device.name || device.deviceId}`}
                >
                  <EllipsisIcon />
                </Button>
              }
            />
            <DropdownMenuContent align="end" className="w-48">
              {device.approval === "pending" && (
                <>
                  <DropdownMenuItem onClick={() => void onApprove(device)}>
                    <CheckIcon />
                    Approve device
                  </DropdownMenuItem>
                  <DropdownMenuSeparator />
                </>
              )}

              {/* Disabled rather than absent when there is nothing to open, so
                  the menu keeps one shape and says why instead of quietly
                  offering less. A pending device has no pipe and no pages; an
                  offline one has no pipe to fetch its page over, and a dead
                  link is worse than a greyed-out one. */}
              {online && device.approval === "approved" ? (
                <DropdownMenuItem
                  render={
                    <a
                      href={deviceUiUrl(device.deviceId)}
                      target="_blank"
                      rel="noreferrer"
                    />
                  }
                >
                  <ExternalLinkIcon />
                  Open device UI
                </DropdownMenuItem>
              ) : (
                <DropdownMenuItem
                  disabled
                  title={
                    device.approval === "pending"
                      ? "Approve this device first"
                      : "Offline — no pipe to load its page over"
                  }
                >
                  <ExternalLinkIcon />
                  Open device UI
                </DropdownMenuItem>
              )}

              <DropdownMenuSeparator />

              <DropdownMenuItem
                variant="destructive"
                onClick={() => void onForget(device)}
              >
                <Trash2Icon />
                {device.approval === "pending"
                  ? "Reject device"
                  : "Forget device"}
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        </div>
      </TableCell>
    </TableRow>
  )
}
