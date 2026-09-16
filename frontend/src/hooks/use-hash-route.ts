import { useCallback, useEffect, useMemo, useState } from "react"

import { RELAY_PAGES, type RelayPage } from "@/components/app/navigation"

/**
 * Where the dashboard is, in the URL.
 *
 * A hash router rather than paths, and for once that is not a preference. The relay
 * already answers `/devices/<id>/…` with a device's OWN files, proxied over that
 * device's pipe — so a real path router here would be competing with the file route
 * for the same URL space. The hash is invisible to the server: every dashboard URL is
 * `/` as far as Kestrel is concerned, and everything after `#` is this shell's
 * business alone.
 *
 * There is no device route here any more. A device's pages are its own site, opened
 * at `/devices/<id>/` — a real URL the server answers, not a destination inside this
 * shell — so the only routes left are the relay's own three pages. What went with the
 * device route was the module host: this shell no longer composes a device's UI out of
 * bundles its firmware ships, it just sends you to the UI the device already serves.
 */
export const HOME: RelayPage = "devices"

const RELAY_IDS = new Set<string>(RELAY_PAGES.map((page) => page.id))

export function routeHash(page: RelayPage): string {
  return `#/${page}`
}

function parse(hash: string): RelayPage {
  const first = hash
    .replace(/^#\/?/, "")
    .split("/")
    .filter((part) => part.length > 0)
    .map(decodeURIComponent)[0]

  // An unknown page lands on Devices rather than on an error screen: a stale
  // bookmark is not a fault, and the list is always a useful place to be. Which is
  // also what happens to a bookmarked `#/devices/<id>/<page>` from when this shell
  // had device pages — it reads as `devices` and opens the list.
  if (first && RELAY_IDS.has(first)) return first as RelayPage
  return HOME
}

export function useHashRoute() {
  const [hash, setHash] = useState(() => window.location.hash)

  useEffect(() => {
    const onChange = () => setHash(window.location.hash)
    window.addEventListener("hashchange", onChange)
    return () => window.removeEventListener("hashchange", onChange)
  }, [])

  const page = useMemo(() => parse(hash), [hash])

  /// Navigate. Writing `location.hash` is what fires `hashchange`, so this is the
  /// only place that has to know the two are connected — and the back button works
  /// without anything here being aware of it.
  const navigate = useCallback((next: RelayPage) => {
    const target = routeHash(next)
    if (window.location.hash === target) return
    window.location.hash = target
  }, [])

  return { page, navigate }
}
