import { useEffect, useState } from "react"

/**
 * Re-renders on a timer so relative times stay honest.
 *
 * Without this, "just now" is whatever it was when the list was last fetched and
 * stays that way: the relay pushes when devices CHANGE, and time passing is not
 * a change it knows about. That is not a cosmetic problem — a connected device
 * reading "last seen 6 minutes ago" says the pipe is dead when it is fine.
 *
 * A tick rather than a refetch: the timestamps are already correct, it is only
 * the rendering of them that goes stale.
 */
export function useNow(intervalMs = 15_000): number {
  const [now, setNow] = useState(() => Date.now())

  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), intervalMs)
    return () => clearInterval(timer)
  }, [intervalMs])

  return now
}
