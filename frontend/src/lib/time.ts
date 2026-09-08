function plural(value: number, unit: string): string {
  const rounded = Math.round(value)
  return `${rounded} ${unit}${rounded === 1 ? "" : "s"} ago`
}

/**
 * "4 minutes ago". The relay sends UTC instants with a trailing Z, so every
 * timestamp is absolute on the wire and turning it into local time is the
 * browser's job.
 */
export function ago(iso: string | null): string {
  if (!iso) return "never"

  const seconds = Math.max(0, (Date.now() - Date.parse(iso)) / 1000)
  if (seconds < 45) return "just now"

  const minutes = seconds / 60
  if (minutes < 60) return plural(minutes, "minute")

  const hours = minutes / 60
  if (hours < 24) return plural(hours, "hour")

  return plural(hours / 24, "day")
}

/** An absolute local time, for the title beside the relative one. */
export function absolute(iso: string | null): string {
  return iso ? new Date(iso).toLocaleString() : "never"
}

/**
 * "4m", "3h 12m", "2d" — how long something has been the case, as opposed to how
 * long ago it happened. Used for an open pipe's age, where "connected 4 minutes
 * ago" would read as though the connection were an event in the past rather than
 * a state still running.
 */
export function duration(iso: string | null): string {
  if (!iso) return "—"

  const minutes = Math.max(0, (Date.now() - Date.parse(iso)) / 60_000)
  if (minutes < 1) return "<1m"
  if (minutes < 60) return `${Math.floor(minutes)}m`

  const hours = Math.floor(minutes / 60)
  if (hours < 24) {
    const rest = Math.floor(minutes % 60)
    return rest ? `${hours}h ${rest}m` : `${hours}h`
  }

  return `${Math.floor(hours / 24)}d`
}

/** "3.8 MB" — sizes as an operator reads them, not as bytes. */
export function bytes(value: number): string {
  if (value < 1024) return `${value} B`
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`
  return `${(value / (1024 * 1024)).toFixed(1)} MB`
}
