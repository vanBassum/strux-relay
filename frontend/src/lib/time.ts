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
