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
/// Which page of a device is showing: a page its firmware declared, by id.
///
/// This was briefly a union — `console` and `settings` were pages the SHELL provided
/// for any device, alongside module pages and addressed under `module/` to keep the id
/// spaces apart. Both are modules now, so there is one kind again and no separate id
/// space to protect: **this shell contributes nothing to a device's navigation.**
///
/// `null` is "no page named", which is not a default. There is no page this build can
/// name; until the manifest arrives there is nowhere to be, and once it has arrived the
/// first page it declares is where we go.
export type DevicePage = { kind: "module"; id: string }

export type Route =
  | { kind: "relay"; page: RelayPage }
  | { kind: "device"; deviceId: string; page: DevicePage | null }

export const HOME: Route = { kind: "relay", page: "devices" }

export function sameDevicePage(a: DevicePage | null, b: DevicePage | null): boolean {
  if (a === null || b === null) return a === b
  return a.id === b.id
}

const RELAY_IDS = new Set<string>(RELAY_PAGES.map((page) => page.id))

/// The hash a route is written as. `#/devices/<id>/<page>` — the id is encoded
/// because a device may name itself anything, and the page id comes from a manifest,
/// which is to say from firmware rather than from this build.
export function routeHash(route: Route): string {
  if (route.kind === "relay") return `#/${route.page}`
  const id = encodeURIComponent(route.deviceId)
  return route.page
    ? `#/devices/${id}/${encodeURIComponent(route.page.id)}`
    : `#/devices/${id}`
}

function parse(hash: string): Route {
  const parts = hash
    .replace(/^#\/?/, "")
    .split("/")
    .filter((part) => part.length > 0)
    .map(decodeURIComponent)

  if (parts.length === 0) return HOME

  if (parts[0] === "devices" && parts[1]) {
    const deviceId = parts[1]
    // Not validated against the manifest here: it arrives over the wire, after the
    // first render, and a route that waited for it would flash a different page on
    // every reload. App resolves the id, and DevicePage reports an unknown one.
    return parts[2]
      ? { kind: "device", deviceId, page: { kind: "module", id: parts[2] } }
      : { kind: "device", deviceId, page: null }
  }

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
