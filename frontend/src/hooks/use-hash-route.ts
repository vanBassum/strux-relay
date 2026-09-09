import { useCallback, useEffect, useMemo, useState } from "react"

import { RELAY_PAGES, type RelayPage } from "@/components/app/navigation"

/**
 * Where the dashboard is, in the URL.
 *
 * A hash router rather than paths, and for once that is not a preference. The relay
 * already answers `/devices/<id>/…` with a device's OWN files, proxied over that
 * device's pipe — so a real path router here would be competing with the file route
 * for the same URL space, and `/devices/x/modules/led.js` would have to be both a
 * dashboard route and a bundle fetch. The hash is invisible to the server: every
 * dashboard URL is `/` as far as Kestrel is concerned, and everything after `#` is
 * this shell's business alone.
 *
 * It also replaces a `useState` view, which is the reason this exists at all now.
 * With nav coming from a device's manifest, a device page is a real destination: it
 * has to survive a refresh, be linkable, and answer the back button — none of which
 * component state can do.
 */
export type Route =
  | { kind: "relay"; page: RelayPage }
  /** `page` is null until a manifest says which pages exist; the shell picks the first. */
  | { kind: "device"; deviceId: string; page: string | null }

export const HOME: Route = { kind: "relay", page: "devices" }

const RELAY_IDS = new Set<string>(RELAY_PAGES.map((page) => page.id))

/// The hash a route is written as. `#/devices/<id>/<page>` — the id is encoded
/// because a device may name itself anything, and the page id comes from a manifest,
/// which is to say from firmware rather than from this build.
export function routeHash(route: Route): string {
  if (route.kind === "relay") return `#/${route.page}`
  const id = encodeURIComponent(route.deviceId)
  return route.page ? `#/devices/${id}/${encodeURIComponent(route.page)}` : `#/devices/${id}`
}

function parse(hash: string): Route {
  const parts = hash
    .replace(/^#\/?/, "")
    .split("/")
    .filter((part) => part.length > 0)
    .map(decodeURIComponent)

  if (parts.length === 0) return HOME

  if (parts[0] === "devices" && parts[1])
    return { kind: "device", deviceId: parts[1], page: parts[2] ?? null }

  // An unknown relay page lands on Devices rather than on an error screen: a stale
  // bookmark is not a fault, and the list is always a useful place to be.
  if (RELAY_IDS.has(parts[0])) return { kind: "relay", page: parts[0] as RelayPage }

  return HOME
}

export function useHashRoute() {
  const [hash, setHash] = useState(() => window.location.hash)

  useEffect(() => {
    const onChange = () => setHash(window.location.hash)
    window.addEventListener("hashchange", onChange)
    return () => window.removeEventListener("hashchange", onChange)
  }, [])

  const route = useMemo(() => parse(hash), [hash])

  /// Navigate. Writing `location.hash` is what fires `hashchange`, so this is the
  /// only place that has to know the two are connected — and the back button works
  /// without anything here being aware of it.
  const navigate = useCallback((next: Route) => {
    const target = routeHash(next)
    if (window.location.hash === target) return
    window.location.hash = target
  }, [])

  /// Same destination, without a history entry. For corrections the user did not ask
  /// for — resolving `#/devices/x` to that device's actual first page, say, which
  /// would otherwise put a step in the back stack that immediately redirects again.
  const replace = useCallback((next: Route) => {
    const target = routeHash(next)
    if (window.location.hash === target) return
    window.history.replaceState(null, "", target)
    setHash(target)
  }, [])

  return { route, navigate, replace }
}
