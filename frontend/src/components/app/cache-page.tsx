import { useMemo, useState } from "react"
import { FlameIcon, RefreshCwIcon, SearchIcon, Trash2Icon } from "lucide-react"

import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import {
  useCache,
  type CacheStats,
  type DeviceCache,
  type DeviceCacheStatus,
} from "@/hooks/use-cache"
import { useNow } from "@/hooks/use-now"
import { absolute, ago, bytes } from "@/lib/time"

const STATUS_LABELS: Record<DeviceCacheStatus, string> = {
  ready: "Ready",
  partial: "Partial",
  empty: "Empty",
  error: "Error",
}

const STATUS_DOTS: Record<DeviceCacheStatus, string> = {
  ready: "bg-emerald-500",
  partial: "bg-amber-500",
  empty: "bg-muted-foreground/40",
  error: "bg-destructive",
}

function Stat({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <div className="min-w-28">
      <div className="text-muted-foreground text-xs">{label}</div>
      <div className="font-mono text-sm tabular-nums">{value}</div>
      {hint && <div className="text-muted-foreground text-xs">{hint}</div>}
    </div>
  )
}

function StatsCard({ stats }: { stats: CacheStats }) {
  const used = stats.maxBytes > 0 ? (stats.bytes / stats.maxBytes) * 100 : 0
  const asked = stats.hits + stats.misses

  return (
    <Card>
      <CardHeader className="flex-row items-center justify-between">
        <CardTitle>Cache</CardTitle>
        <span className="text-muted-foreground text-xs">
          in memory, since start
        </span>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        <div className="flex flex-wrap gap-x-6 gap-y-3">
          <Stat
            label="Total size"
            value={bytes(stats.bytes)}
            hint={`of ${bytes(stats.maxBytes)} · ${used.toFixed(0)}%`}
          />
          <Stat
            label="Devices cached"
            value={`${stats.devicesWithContent} / ${stats.devicesKnown}`}
          />
          <Stat label="Files" value={stats.files.toLocaleString()} />
          {/* A hit rate is the answer to "is the cache actually being used", so
              it is worth computing rather than leaving two numbers side by side. */}
          <Stat
            label="Hits"
            value={stats.hits.toLocaleString()}
            hint={asked > 0 ? `${((stats.hits / asked) * 100).toFixed(0)}% of requests` : undefined}
          />
          <Stat label="Misses" value={stats.misses.toLocaleString()} />
          <Stat label="Served" value={bytes(stats.bytesServed)} hint="from cache" />
          <Stat label="Fetched" value={bytes(stats.bytesFetched)} hint="from devices" />
          <Stat label="Failed fetches" value={stats.failedFetches.toLocaleString()} />
        </div>

        <div className="bg-muted h-1.5 w-full overflow-hidden rounded-full">
          <div
            className="bg-primary h-full rounded-full"
            style={{ width: `${Math.min(100, used)}%` }}
          />
        </div>
      </CardContent>
    </Card>
  )
}

export function CachePage() {
  const { cache, busy, warmDevice, clearDevice, warmAll, clearAll } = useCache()
  const [query, setQuery] = useState("")
  useNow()

  const shown = useMemo(() => {
    const needle = query.trim().toLowerCase()
    if (!cache) return []
    if (!needle) return cache.devices
    return cache.devices.filter((device) =>
      [device.name, device.deviceId].some((field) =>
        field.toLowerCase().includes(needle)
      )
    )
  }, [cache, query])

  if (!cache) {
    return <div className="text-muted-foreground p-4 text-sm">Loading…</div>
  }

  const { policy } = cache
  const anyOnline = cache.devices.some((device) => device.online)

  return (
    <div className="flex flex-col gap-4 p-4">
      <StatsCard stats={cache.stats} />

      <div className="grid gap-4 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>Policy</CardTitle>
          </CardHeader>
          <CardContent>
            {/* Read off the implementation, so there is no TTL row: a connection
                is the cache's lifetime, and showing a plausible TTL would
                describe a cache the relay does not have. */}
            <dl className="flex flex-col gap-2 text-sm">
              {[
                ["Immutable assets", policy.immutableAssets],
                ["Mutable files", policy.mutableFiles],
                ["Server-side lifetime", policy.serverLifetime],
                ["Maximum size", bytes(policy.maxBytes)],
                ["Eviction", policy.eviction],
                ["Warm on connect", policy.warmOnConnect ? "Yes" : "No"],
              ].map(([label, value]) => (
                <div key={label as string}>
                  <dt className="text-muted-foreground text-xs">{label}</dt>
                  <dd>{value}</dd>
                </div>
              ))}
            </dl>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Actions</CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col items-start gap-2">
            <Button
              variant="outline"
              disabled={busy !== null || !anyOnline}
              onClick={() => void warmAll()}
            >
              <FlameIcon />
              {busy === "warm:all" ? "Warming…" : "Warm all connected"}
            </Button>

            <Dialog>
              <DialogTrigger
                render={
                  <Button variant="outline" disabled={busy !== null}>
                    <Trash2Icon />
                    Clear all
                  </Button>
                }
              />
              <DialogContent>
                <DialogHeader>
                  <DialogTitle>Clear every cached file?</DialogTitle>
                  <DialogDescription>
                    Nothing is lost — the files live on the devices. Every device
                    page will be slow again until its cache refills, one pipe
                    round trip per file.
                  </DialogDescription>
                </DialogHeader>
                <DialogFooter>
                  <DialogClose render={<Button variant="outline">Cancel</Button>} />
                  <DialogClose
                    render={
                      <Button onClick={() => void clearAll()}>Clear all</Button>
                    }
                  />
                </DialogFooter>
              </DialogContent>
            </Dialog>

            {!anyOnline && (
              <p className="text-muted-foreground text-xs">
                Warming reads from a device, so it needs one connected.
              </p>
            )}
          </CardContent>
        </Card>
      </div>

      <Card>
        <CardHeader className="flex-row items-center justify-between gap-2">
          <CardTitle>Devices</CardTitle>
          <div className="relative max-w-xs flex-1 basis-56">
            <SearchIcon className="text-muted-foreground pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2" />
            <Input
              className="pl-8"
              placeholder="Search devices…"
              value={query}
              onChange={(event) => setQuery(event.target.value)}
            />
          </div>
        </CardHeader>
        <CardContent>
          <div className="rounded-lg border">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Device</TableHead>
                  <TableHead>Status</TableHead>
                  <TableHead>Size</TableHead>
                  <TableHead>Files</TableHead>
                  <TableHead>Last warmed</TableHead>
                  <TableHead>Last used</TableHead>
                  <TableHead className="text-right">Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {shown.length === 0 ? (
                  <TableRow>
                    <TableCell colSpan={7} className="text-muted-foreground py-8 text-center">
                      {cache.devices.length === 0
                        ? "No approved devices yet."
                        : "Nothing matches that search."}
                    </TableCell>
                  </TableRow>
                ) : (
                  shown.map((device) => (
                    <CacheRow
                      key={device.deviceId}
                      device={device}
                      busy={busy}
                      onWarm={warmDevice}
                      onClear={clearDevice}
                    />
                  ))
                )}
              </TableBody>
            </Table>
          </div>
        </CardContent>
      </Card>
    </div>
  )
}

function CacheRow({
  device,
  busy,
  onWarm,
  onClear,
}: {
  device: DeviceCache
  busy: string | null
  onWarm: (device: DeviceCache) => Promise<void>
  onClear: (device: DeviceCache) => Promise<void>
}) {
  const warming = busy === `warm:${device.deviceId}`

  return (
    <TableRow>
      <TableCell>
        <div className="flex items-center gap-1.5">
          <span
            className={`size-1.5 shrink-0 rounded-full ${device.online ? "bg-emerald-500" : "bg-muted-foreground/40"}`}
            title={device.online ? "Online" : "Offline"}
          />
          <span className="font-medium">{device.name}</span>
        </div>
        <div className="text-muted-foreground font-mono text-xs">
          {device.deviceId}
        </div>
      </TableCell>
      <TableCell>
        <span className="flex items-center gap-1.5" title={device.lastError ?? undefined}>
          <span className={`size-1.5 rounded-full ${STATUS_DOTS[device.status]}`} />
          {STATUS_LABELS[device.status]}
        </span>
        {/* Partial is only useful if it says how partial. */}
        {device.status === "partial" && device.expectedFiles > 0 && (
          <span className="text-muted-foreground text-xs">
            {device.warmedFiles} of {device.expectedFiles}
          </span>
        )}
      </TableCell>
      <TableCell className="font-mono text-xs">{bytes(device.bytes)}</TableCell>
      <TableCell className="font-mono text-xs tabular-nums">{device.files}</TableCell>
      <TableCell title={absolute(device.lastWarmedAt)}>
        {device.lastWarmedAt ? ago(device.lastWarmedAt) : "never"}
      </TableCell>
      <TableCell title={absolute(device.lastUsedAt)}>
        {device.lastUsedAt ? ago(device.lastUsedAt) : "never"}
      </TableCell>
      <TableCell className="text-right">
        <div className="flex justify-end gap-1">
          <Button
            size="sm"
            variant="outline"
            // Warming reads FROM the device, so an offline one has nothing to give.
            disabled={busy !== null || !device.online}
            title={device.online ? undefined : "Device is not connected"}
            onClick={() => void onWarm(device)}
          >
            <RefreshCwIcon className={warming ? "animate-spin" : undefined} />
            {warming ? "Warming…" : "Warm"}
          </Button>
          <Button
            size="sm"
            variant="outline"
            disabled={busy !== null || device.files === 0}
            onClick={() => void onClear(device)}
          >
            <Trash2Icon />
            Clear
          </Button>
        </div>
      </TableCell>
    </TableRow>
  )
}
