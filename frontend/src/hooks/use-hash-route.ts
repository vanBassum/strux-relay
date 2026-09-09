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
/// Which page of a device is showing.
///
/// A union rather than one string, because the two halves come from different places
/// and can collide: `console` and `settings` are THIS shell's pages for any device,
/// while a module page id comes from firmware, which could perfectly reasonably
/// declare a page called "settings". So a module page is addressed under `module/`,
/// the same separation Strux's own shell makes with `#/module/<id>`.
///
/// `null` is the overview — the device's contributed cards — which is the landing page
/// and needs no name in the URL.
export type DevicePage =
  | { kind: "shell"; page: DeviceShellPage }
  | { kind: "module"; id: string }

/// Pages this shell provides for every device, whatever its firmware contributes.
/// They are framework features — `settings list` and `log list` exist on every Strux
/// device and describe themselves — so they are not modules and never will be.
export const DEVICE_SHELL_PAGES = ["console", "settings", "firmware"] as const
export type DeviceShellPage = (typeof DEVICE_SHELL_PAGES)[number]

function isDeviceShellPage(value: string): value is DeviceShellPage {
  return (DEVICE_SHELL_PAGES as readonly string[]).includes(value)
}

export type Route =
  | { kind: "relay"; page: RelayPage }
  | { kind: "device"; deviceId: string; page: DevicePage | null }

export const HOME: Route = { kind: "relay", page: "devices" }

export function sameDevicePage(a: DevicePage | null, b: DevicePage | null): boolean {
  if (a === null || b === null) return a === b
  if (a.kind !== b.kind) return false
  return a.kind === "module" && b.kind === "module"
    ? a.id === b.id
    : a.kind === "shell" && b.kind === "shell" && a.page === b.page
}

const RELAY_IDS = new Set<string>(RELAY_PAGES.map((page) => page.id))

/// The hash a route is written as. `#/devices/<id>/<page>` — the id is encoded
/// because a device may name itself anything, and the page id comes from a manifest,
/// which is to say from firmware rather than from this build.
export function routeHash(route: Route): string {
  if (route.kind === "relay") return `#/${route.page}`
  const id = encodeURIComponent(route.deviceId)
  if (!route.page) return `#/devices/${id}`
  return route.page.kind === "module"
    ? `#/devices/${id}/module/${encodeURIComponent(route.page.id)}`
    : `#/devices/${id}/${route.page.page}`
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
    if (!parts[2]) return { kind: "device", deviceId, page: null }
    if (parts[2] === "module")
      // Not validated against the manifest here: the manifest arrives over the wire,
      // after the first render, and a route that waited for it would flash the
      // overview on every reload of a module page. DevicePage resolves the id and
      // reports an unknown one.
      return parts[3]
        ? { kind: "device", deviceId, page: { kind: "module", id: parts[3] } }
        : { kind: "device", deviceId, page: null }
    if (isDeviceShellPage(parts[2]))
      return { kind: "device", deviceId, page: { kind: "shell", page: parts[2] } }
    // An unknown page falls back to the overview, which always exists.
    return { kind: "device", deviceId, page: null }
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
