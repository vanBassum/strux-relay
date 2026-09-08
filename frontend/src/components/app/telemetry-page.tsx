import { DatabaseIcon, PauseIcon, PlayIcon, Trash2Icon } from "lucide-react"

import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import { useNow } from "@/hooks/use-now"
import {
  useTelemetry,
  type SinkState,
  type TelemetryStatus,
} from "@/hooks/use-telemetry"
import { ago } from "@/lib/time"

const SINK_LABELS: Record<SinkState, string> = {
  notConfigured: "Not configured",
  ready: "Ready",
  connected: "Connected",
  error: "Error",
}

function SinkBadge({ state }: { state: SinkState }) {
  // "Ready" is its own state rather than being folded into Connected: configured
  // but never yet written to is not the same as proven to work.
  const variant =
    state === "connected"
      ? "secondary"
      : state === "error"
        ? "destructive"
        : "outline"

  return <Badge variant={variant}>{SINK_LABELS[state]}</Badge>
}

function Stat({
  label,
  value,
  hint,
}: {
  label: string
  value: string
  hint?: string
}) {
  return (
    <div className="min-w-24">
      <div className="text-muted-foreground text-xs">{label}</div>
      <div className="font-mono text-sm tabular-nums" title={hint}>
        {value}
      </div>
    </div>
  )
}

function SinkCard({ status }: { status: TelemetryStatus | null }) {
  return (
    <Card>
      <CardHeader className="flex-row items-center gap-2">
        <DatabaseIcon className="text-muted-foreground size-4" />
        <CardTitle>{status?.sink.name ?? "Sink"}</CardTitle>
        {status && <SinkBadge state={status.sink.state} />}
      </CardHeader>
      <CardContent className="flex flex-col gap-2">
        {status ? (
          <>
            <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
              {status.sink.details.map((detail) => (
                <div key={detail.label} className="col-span-2 grid grid-cols-subgrid">
                  <dt className="text-muted-foreground">{detail.label}</dt>
                  <dd className="truncate font-mono text-xs" title={detail.value}>
                    {detail.value}
                  </dd>
                </div>
              ))}
            </dl>
            {status.sink.state === "notConfigured" && (
              <p className="text-muted-foreground text-xs">
                Points are still received, counted and shown below — they are just
                not stored anywhere. Set{" "}
                <code className="font-mono">Relay:Telemetry:Influx</code> to
                forward them.
              </p>
            )}
            {status.sink.lastError && (
              <p className="text-destructive text-xs">
                {status.sink.lastError}{" "}
                <span className="text-muted-foreground">
                  ({ago(status.sink.lastErrorAt)})
                </span>
              </p>
            )}
          </>
        ) : (
          <p className="text-muted-foreground text-sm">Loading…</p>
        )}
      </CardContent>
    </Card>
  )
}

function CountersCard({ status }: { status: TelemetryStatus | null }) {
  const counters = status?.counters

  return (
    <Card>
      <CardHeader className="flex-row items-center justify-between">
        <CardTitle>Counters</CardTitle>
        {/* Said out loud, because a counter that resets on restart is otherwise
            read as a number that means something historical. */}
        <span className="text-muted-foreground text-xs">
          in memory, since start
        </span>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        <div className="flex flex-wrap gap-x-6 gap-y-3">
          <Stat label="Received" value={counters?.received.toLocaleString() ?? "—"} />
          <Stat label="Forwarded" value={counters?.forwarded.toLocaleString() ?? "—"} />
          <Stat label="Dropped" value={counters?.dropped.toLocaleString() ?? "—"} />
          <Stat label="Failed" value={counters?.failed.toLocaleString() ?? "—"} />
          <Stat
            label="Rate"
            value={counters ? `${counters.ratePerSecond.toFixed(1)}/s` : "—"}
            hint="Events received over the last 10 seconds"
          />
          <Stat label="Last event" value={ago(counters?.lastEventAt ?? null)} />
          <Stat label="Last forward" value={ago(counters?.lastForwardAt ?? null)} />
        </div>

        <div>
          <div className="text-muted-foreground mb-1 text-xs">Dropped by reason</div>
          <div className="flex flex-wrap gap-x-6 gap-y-2">
            <Stat
              label="No sink configured"
              value={counters?.drops.noSinkConfigured.toLocaleString() ?? "—"}
            />
            <Stat
              label="Invalid event"
              value={counters?.drops.invalidEvent.toLocaleString() ?? "—"}
            />
            <Stat
              label="Queue full"
              value={counters?.drops.queueFull.toLocaleString() ?? "—"}
            />
          </div>
        </div>
      </CardContent>
    </Card>
  )
}

export function TelemetryPage() {
  const { status, events, paused, setPaused, clear, capacity } = useTelemetry()
  // Keeps the relative times in the cards honest between status pushes.
  useNow()

  return (
    <div className="flex flex-col gap-4 p-4">
      <div className="grid gap-4 lg:grid-cols-2">
        <SinkCard status={status} />
        <CountersCard status={status} />
      </div>

      <Card>
        <CardHeader className="flex-row items-center justify-between gap-2">
          <div>
            <CardTitle>Live events</CardTitle>
            <p className="text-muted-foreground text-xs">
              Not stored anywhere — the newest {capacity} are held by this browser
              and go on refresh.
            </p>
          </div>
          <div className="flex shrink-0 gap-1">
            <Button
              size="sm"
              variant="outline"
              onClick={() => setPaused(!paused)}
            >
              {paused ? <PlayIcon /> : <PauseIcon />}
              {paused ? "Resume" : "Pause"}
            </Button>
            <Button size="sm" variant="outline" onClick={clear}>
              <Trash2Icon />
              Clear
            </Button>
          </div>
        </CardHeader>
        <CardContent>
          <div className="max-h-[26rem] overflow-y-auto rounded-lg border">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead className="w-28">Time</TableHead>
                  <TableHead className="w-40">Device</TableHead>
                  <TableHead className="w-44">Measurement</TableHead>
                  <TableHead>Payload</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {events.length === 0 ? (
                  <TableRow>
                    <TableCell colSpan={4} className="text-muted-foreground py-8 text-center">
                      {paused
                        ? "Paused."
                        : "Waiting for telemetry. A device sends nothing until telem.enabled is set."}
                    </TableCell>
                  </TableRow>
                ) : (
                  events.map((event) => (
                    <TableRow key={event.sequence}>
                      <TableCell
                        className="font-mono text-xs"
                        title={new Date(event.at).toLocaleString()}
                      >
                        {new Date(event.at).toLocaleTimeString(undefined, {
                          hour12: false,
                        })}
                      </TableCell>
                      <TableCell className="truncate" title={event.deviceId}>
                        {event.deviceName || event.deviceId}
                      </TableCell>
                      <TableCell>
                        <Badge variant="outline" className="font-mono text-xs">
                          {event.measurement}
                        </Badge>
                      </TableCell>
                      <TableCell className="font-mono text-xs">
                        {event.payload}
                        {event.tags && (
                          <span className="text-muted-foreground"> · {event.tags}</span>
                        )}
                      </TableCell>
                    </TableRow>
                  ))
                )}
              </TableBody>
            </Table>
          </div>
          {paused && events.length > 0 && (
            <p className="text-muted-foreground mt-2 text-xs">
              Paused — arriving events are still forwarded and counted, just not
              added here.
            </p>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
