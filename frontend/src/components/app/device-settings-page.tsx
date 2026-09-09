import { useCallback, useEffect, useMemo, useState } from "react"
import { LockIcon, SaveIcon, Undo2Icon } from "lucide-react"
import { toast } from "sonner"

import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Switch } from "@/components/ui/switch"
import { Skeleton } from "@/components/ui/skeleton"
import type { Device } from "@/hooks/use-devices"
import { useRelayContext } from "@/hooks/use-relay"
import { deviceTransport } from "@/shell/device-transport"

/**
 * A device's settings, edited through the relay.
 *
 * This is **not** a module and will not become one. `settings list` already describes
 * itself — every entry arrives with its key, label, type and value — so the page is
 * generated from a declaration rather than drawn by code only the firmware could
 * supply. That is exactly the line the module system draws: modules are for the view a
 * shell *could not* have described. A settings module would also mean every device
 * shipping the framework's own UI, so a fork could omit its own settings page.
 *
 * Deliberately narrower than the device shell's version, which also offers a raw JSON
 * editor and a Wi-Fi network picker. Both are conveniences that pull real weight
 * (prismjs, a scan command whose results are only meaningful on the device's own LAN),
 * and neither is needed to change a value from across the internet. The device's own
 * page is one click away for those.
 */
type SettingType = "bool" | "string" | (string & {})

interface SettingEntry {
  key: string
  label: string
  type: SettingType
  value: unknown
}

const NUMERIC = new Set([
  "uint8",
  "int8",
  "uint16",
  "int16",
  "uint32",
  "int32",
  "uint64",
  "int64",
  "float",
  "double",
])

/// Which values not to put on screen in clear text.
///
/// A heuristic on the key, and it is a mitigation rather than a fix: the device sends
/// these in the clear because `settings list` has no notion of a secret yet (it is a
/// known item on the relay-in-production plan). Masking here stops a token being read
/// over somebody's shoulder; it does not stop it being in the reply.
function isSecret(key: string): boolean {
  return /password|token|secret|key$/i.test(key)
}

function groupOf(key: string): string {
  const dot = key.indexOf(".")
  return dot > 0 ? key.slice(0, dot) : "general"
}

function groupLabel(prefix: string): string {
  const known: Record<string, string> = {
    wifi: "Wi-Fi",
    web: "Web interface",
    relay: "Relay",
    telem: "Telemetry",
    ntp: "Time & NTP",
    led: "LED",
  }
  return known[prefix] ?? prefix.charAt(0).toUpperCase() + prefix.slice(1)
}

export function DeviceSettingsPage({ device }: { device: Device }) {
  const { invoke } = useRelayContext()
  const transport = deviceTransport(invoke, device.deviceId)

  const [entries, setEntries] = useState<SettingEntry[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  // Only what the operator actually changed, keyed by setting key. An empty map means
  // there is nothing to save, which is also what disables the button — so "dirty" is
  // derived from the edits rather than tracked beside them and able to disagree.
  const [edits, setEdits] = useState<Record<string, unknown>>({})
  const [saving, setSaving] = useState(false)
  const [revealed, setRevealed] = useState<Record<string, boolean>>({})

  const load = useCallback(async () => {
    try {
      const reply = await transport.request<{ settings: SettingEntry[] }>("settings list")
      setEntries(reply?.settings ?? [])
      setEdits({})
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }, [transport])

  useEffect(() => {
    void load()
  }, [load])

  const groups = useMemo(() => {
    if (!entries) return []
    const byPrefix = new Map<string, SettingEntry[]>()
    for (const entry of entries) {
      const prefix = groupOf(entry.key)
      if (!byPrefix.has(prefix)) byPrefix.set(prefix, [])
      byPrefix.get(prefix)!.push(entry)
    }
    return [...byPrefix.entries()]
      .map(([prefix, items]) => ({ prefix, label: groupLabel(prefix), items }))
      .sort((a, b) => a.label.localeCompare(b.label))
  }, [entries])

  const dirty = Object.keys(edits)

  const save = async () => {
    setSaving(true)
    try {
      // One `settings set` per changed key, then a single `settings save`. Sequential
      // rather than parallel, and not because it is tidier: every one of these takes
      // the device's single in-flight pipe, so firing them together would only queue
      // — and a failure halfway through would leave no way to say which key it was.
      for (const key of dirty) {
        const value = edits[key]
        await transport.request("settings set", {
          key,
          value: typeof value === "boolean" ? (value ? "1" : "0") : String(value),
        })
      }
      await transport.request("settings save")
      toast.success(
        dirty.length === 1
          ? `Saved ${dirty[0]}.`
          : `Saved ${dirty.length} settings on ${device.name || device.deviceId}.`,
      )
      // Re-read rather than trusting the writes: the device coerces values (an
      // out-of-range number, a string too long for its field) and what it kept is the
      // only thing worth showing.
      await load()
    } catch (e) {
      toast.error(e instanceof Error ? e.message : String(e))
    } finally {
      setSaving(false)
    }
  }

  if (device.connection !== "online")
    return (
      <Notice>
        This device is offline, so there is no pipe to read or write its settings over.
      </Notice>
    )

  if (error)
    return (
      <div className="space-y-3 p-4">
        <Notice>{error}</Notice>
        <Button size="sm" variant="outline" onClick={() => void load()}>
          Try again
        </Button>
      </div>
    )

  if (!entries)
    return (
      <div className="mx-auto max-w-2xl space-y-3 p-4">
        <Skeleton className="h-8 w-40" />
        <Skeleton className="h-24 w-full" />
        <Skeleton className="h-24 w-full" />
      </div>
    )

  return (
    <div className="mx-auto w-full max-w-2xl space-y-6 p-4">
      <div className="flex items-center justify-between gap-4">
        <div>
          <h2 className="text-lg font-semibold">Settings</h2>
          <p className="text-muted-foreground text-sm">
            Read off the device with <code className="font-mono text-xs">settings list</code>,
            so this page is whatever that firmware declares.
          </p>
        </div>
        <div className="flex shrink-0 gap-2">
          <Button
            size="sm"
            variant="outline"
            disabled={dirty.length === 0 || saving}
            onClick={() => setEdits({})}
          >
            <Undo2Icon />
            Revert
          </Button>
          <Button size="sm" disabled={dirty.length === 0 || saving} onClick={() => void save()}>
            <SaveIcon />
            {saving ? "Saving…" : dirty.length > 0 ? `Save ${dirty.length}` : "Save"}
          </Button>
        </div>
      </div>

      {groups.map((group) => (
        <div
          key={group.prefix}
          className="bg-card text-card-foreground rounded-xl border p-4 shadow-sm"
        >
          <h3 className="mb-3 text-sm font-medium">{group.label}</h3>
          <div className="space-y-3">
            {group.items.map((entry) => {
              const current = entry.key in edits ? edits[entry.key] : entry.value
              const changed = entry.key in edits
              const secret = isSecret(entry.key)

              return (
                <div key={entry.key} className="flex items-center justify-between gap-4">
                  <div className="min-w-0">
                    <div className="flex items-center gap-1.5 text-sm">
                      {secret && <LockIcon className="text-muted-foreground size-3.5" />}
                      <span className={changed ? "font-medium" : undefined}>
                        {entry.label || entry.key}
                      </span>
                    </div>
                    <div className="text-muted-foreground font-mono text-xs">
                      {entry.key}
                    </div>
                  </div>

                  {entry.type === "bool" ? (
                    <Switch
                      checked={current === true || current === 1 || current === "1"}
                      onCheckedChange={(next) =>
                        setEdits((e) => ({ ...e, [entry.key]: next }))
                      }
                    />
                  ) : (
                    <Input
                      className="max-w-56"
                      inputMode={NUMERIC.has(entry.type) ? "numeric" : undefined}
                      // Masked until asked for, and revealing is per field rather
                      // than a page-wide toggle: showing every secret at once to see
                      // one is the thing masking was for.
                      type={secret && !revealed[entry.key] ? "password" : "text"}
                      value={String(current ?? "")}
                      onChange={(event) =>
                        setEdits((e) => ({ ...e, [entry.key]: event.target.value }))
                      }
                      onFocus={() =>
                        secret && setRevealed((r) => ({ ...r, [entry.key]: true }))
                      }
                    />
                  )}
                </div>
              )
            })}
          </div>
        </div>
      ))}
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
