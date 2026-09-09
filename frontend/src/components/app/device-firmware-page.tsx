import { useCallback, useEffect, useRef, useState } from "react"
import { RefreshCwIcon, UploadIcon } from "lucide-react"
import { toast } from "sonner"

import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Skeleton } from "@/components/ui/skeleton"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import type { Device } from "@/hooks/use-devices"
import { useRelayContext } from "@/hooks/use-relay"
import { deviceTransport } from "@/shell/device-transport"

/**
 * A device's partitions, and a way to write one.
 *
 * The table is entirely declarative: `partition list` already says which slot is
 * running, which is next for OTA, and — crucially — which ones are `uploadable`. So
 * this page decides nothing about the device's flash layout; it renders what the
 * device says about itself, which is why it works unchanged on a fork with a
 * different partition table.
 *
 * The upload is the one thing here that needed new plumbing. A command is one
 * envelope and one reply; writing an image is a *session* — an envelope, then the
 * bytes as body chunks, then a single reply — so the relay grew
 * `PartitionUploadAsync` and an HTTP POST route to feed it. HTTP because an upload is
 * a request body: through the hub's JSON protocol the image would travel as base64,
 * a third larger and buffered as strings, where `Request.Body` is a stream the relay
 * reads 4 KB at a time and hands straight to the pipe.
 *
 * Progress is the browser's own upload progress — bytes accepted by the relay. The
 * device also reports its flash position, but surfacing that would need a hub group
 * per upload, and the relay forwards chunk by chunk while awaiting the socket, so the
 * two track each other closely enough. The honest gap is the tail: the last percent
 * sits while the device finishes writing, which is why the button says "Flashing"
 * rather than showing 100%.
 */
interface PartitionEntry {
  label: string
  type: string
  subtype: string
  offset: number
  size: number
  running: boolean
  nextOta: boolean
  uploadable: boolean
  version: string
}

interface StatusReply {
  firmware?: string
  running?: string
  nextSlot?: string
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`
  return `${(bytes / 1024 / 1024).toFixed(2)} MB`
}

export function DeviceFirmwarePage({ device }: { device: Device }) {
  const { invoke } = useRelayContext()
  const transport = deviceTransport(invoke, device.deviceId)

  const [status, setStatus] = useState<StatusReply | null>(null)
  const [partitions, setPartitions] = useState<PartitionEntry[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  // Which partition the file picker is for, and how far the POST has got.
  const [target, setTarget] = useState<PartitionEntry | null>(null)
  const [percent, setPercent] = useState<number | null>(null)
  const picker = useRef<HTMLInputElement | null>(null)

  const load = useCallback(async () => {
    setBusy(true)
    try {
      // Two round trips rather than one: they are separate commands on the device and
      // nothing here needs them to be consistent with each other to the millisecond.
      const [s, p] = [
        await transport.request<StatusReply>("partition status"),
        await transport.request<{ partitions?: PartitionEntry[] }>("partition list"),
      ]
      setStatus(s ?? {})
      setPartitions(p?.partitions ?? [])
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }, [transport])

  useEffect(() => {
    void load()
  }, [load])

  const upload = (partition: PartitionEntry, file: File) => {
    setPercent(0)

    // XHR rather than fetch, for one reason: fetch still has no upload progress
    // event. Everything else about it would be nicer.
    const request = new XMLHttpRequest()
    const activate = partition.type === "app" ? "1" : "0"
    request.open(
      "POST",
      `/devices/${encodeURIComponent(device.deviceId)}/partition/${encodeURIComponent(partition.label)}?activate=${activate}`,
    )
    request.setRequestHeader("Content-Type", "application/octet-stream")

    request.upload.onprogress = (event) => {
      if (event.lengthComputable) setPercent(Math.round((event.loaded / event.total) * 100))
    }

    request.onload = () => {
      setPercent(null)
      if (request.status >= 200 && request.status < 300) {
        toast.success(
          activate === "1"
            ? `Wrote ${file.name} to ${partition.label} and made it the boot slot.`
            : `Wrote ${file.name} to ${partition.label}.`,
        )
        void load()
        return
      }
      // The relay answers a device-side failure as a ProblemDetails 502, so its
      // `detail` is the device's own reason.
      let detail = `HTTP ${request.status}`
      try {
        detail = JSON.parse(request.responseText)?.detail ?? detail
      } catch {
        /* not ProblemDetails — the status is all there is */
      }
      toast.error(detail)
    }

    request.onerror = () => {
      setPercent(null)
      toast.error("The upload could not reach the relay.")
    }

    request.send(file)
  }

  if (device.connection !== "online")
    return (
      <Notice>
        This device is offline. Its partitions are read over the pipe, so there is
        nothing to show and nothing to write.
      </Notice>
    )

  if (error && !partitions)
    return (
      <div className="space-y-3 p-4">
        <Notice>{error}</Notice>
        <Button size="sm" variant="outline" onClick={() => void load()}>
          Try again
        </Button>
      </div>
    )

  return (
    <div className="mx-auto w-full max-w-3xl space-y-6 p-4">
      <input
        ref={picker}
        type="file"
        accept=".bin,application/octet-stream"
        className="hidden"
        onChange={(event) => {
          const file = event.target.files?.[0]
          if (file && target) upload(target, file)
          // Cleared so picking the same file twice in a row still fires a change.
          event.target.value = ""
        }}
      />

      <div className="flex items-start justify-between gap-4">
        <div>
          <h2 className="text-lg font-semibold">Firmware</h2>
          <p className="text-muted-foreground text-sm">
            Read off the device with{" "}
            <code className="font-mono text-xs">partition status</code> and{" "}
            <code className="font-mono text-xs">partition list</code>, so the layout is
            whatever this device says it is.
          </p>
        </div>
        <Button size="sm" variant="outline" disabled={busy} onClick={() => void load()}>
          <RefreshCwIcon />
          Refresh
        </Button>
      </div>

      {status && (
        <div className="bg-card text-card-foreground grid gap-x-8 gap-y-2 rounded-xl border p-4 text-sm shadow-sm sm:grid-cols-3">
          <Field label="Running firmware" value={status.firmware ?? "—"} />
          <Field label="Running slot" value={status.running ?? "—"} />
          <Field label="Next OTA slot" value={status.nextSlot ?? "—"} />
        </div>
      )}

      {percent !== null && (
        <div className="bg-card rounded-xl border p-4 text-sm shadow-sm">
          <div className="mb-2 flex justify-between">
            <span>
              {percent < 100
                ? `Uploading to ${target?.label}…`
                : `Flashing ${target?.label} — the device is still writing`}
            </span>
            <span className="font-mono">{percent}%</span>
          </div>
          <div className="bg-muted h-2 overflow-hidden rounded-full">
            <div
              className="bg-primary h-full transition-[width]"
              style={{ width: `${percent}%` }}
            />
          </div>
        </div>
      )}

      {!partitions ? (
        <Skeleton className="h-48 w-full" />
      ) : (
        <div className="overflow-x-auto rounded-xl border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Partition</TableHead>
                <TableHead>Type</TableHead>
                <TableHead>Size</TableHead>
                <TableHead>Version</TableHead>
                <TableHead className="text-right">Write</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {partitions.map((entry) => (
                <TableRow key={entry.label}>
                  <TableCell className="font-mono text-xs">
                    <div className="flex items-center gap-2">
                      {entry.label}
                      {entry.running && <Badge variant="secondary">running</Badge>}
                      {entry.nextOta && <Badge variant="outline">next OTA</Badge>}
                    </div>
                  </TableCell>
                  <TableCell className="text-xs">
                    {entry.type}
                    <span className="text-muted-foreground">/{entry.subtype}</span>
                  </TableCell>
                  <TableCell className="font-mono text-xs">
                    {formatBytes(entry.size)}
                  </TableCell>
                  <TableCell className="font-mono text-xs">
                    {entry.version || "—"}
                  </TableCell>
                  <TableCell className="text-right">
                    {/* `uploadable` is the DEVICE's answer, not a rule reproduced
                        here: it already refuses to overwrite the slot it is running
                        from, and duplicating that logic in a browser is how the two
                        come to disagree. */}
                    <Button
                      size="sm"
                      variant="outline"
                      disabled={!entry.uploadable || percent !== null}
                      title={
                        entry.uploadable
                          ? undefined
                          : entry.running
                            ? "This is the slot the device is running from"
                            : "The device does not offer this partition for upload"
                      }
                      onClick={() => {
                        setTarget(entry)
                        picker.current?.click()
                      }}
                    >
                      <UploadIcon />
                      Upload
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}

      <p className="text-muted-foreground text-sm">
        An app image is erased, written and then activated, in that order — and only
        activated once every byte landed. Until then the old slot still boots, so a
        failed upload leaves the device running exactly what it was running before.
      </p>
    </div>
  )
}

function Field({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <div className="text-muted-foreground text-xs">{label}</div>
      <div className="font-mono">{value}</div>
    </div>
  )
}

function Notice({ children }: { children: React.ReactNode }) {
  return (
    <div className="p-4">
      <div className="bg-card text-card-foreground max-w-2xl rounded-xl border p-6 text-sm shadow-sm">
        {children}
      </div>
    </div>
  )
}
