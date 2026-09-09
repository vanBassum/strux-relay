// The host side of the module contract, on the relay: read a device's manifest, check
// the version, import a bundle when its page is opened, hand it a ShellProvider, and
// match what it registered against what the firmware declared.
//
// The order is the design. The manifest is a COMMAND, so the sidebar is drawn from it
// before any module code has been fetched; a bundle is imported only when one of its
// pages is actually needed. And every one of those steps is per device, because this
// shell has many.
//
// The bundle itself arrives over HTTP — `import()` is a browser GET and there is no
// way to hand a browser an ES module over a WebSocket short of a blob-URL hack. That
// GET is `/devices/<id>/modules/<id>.js`, which the relay answers by asking the
// device for the file over its pipe. So even module code reaches the browser through
// the socket the device dialled; the only thing HTTP moves is the last hop.

import {
  useCallback,
  useEffect,
  useSyncExternalStore,
  type ReactNode,
} from "react"
import { toast } from "sonner"
import {
  HOST_API,
  type ManifestModule,
  type ShellProvider,
  type UiManifest,
} from "@shell/contract"

import type { Device } from "@/hooks/use-devices"
import { useRelayContext } from "@/hooks/use-relay"
import { deviceTransport, type TransportHub } from "@/shell/device-transport"
import {
  forDevice,
  forget,
  type DeviceModules,
  type ManifestStatus,
} from "@/shell/module-registry"
import { type DeviceNavItem } from "@/lib/device-nav"

/// What the relay's GetDeviceUi answers. Status is the relay's classification — only
/// it can tell a device that REFUSED the command from one that went silent — and the
/// manifest is the device's own reply text, unparsed on the way through.
///
/// The status values are lower-case because the SERVER spells them that way on purpose
/// (see DeviceUiView.Of). They were written here as "Absent"/"Offline" first, matching
/// the C# enum's own casing, and the serializer camel-cased them: every device that
/// refused `ui modules` then fell through to the error branch and was told it had
/// failed to answer. Which is most of a real fleet. Nothing threw, and the one device
/// that did have modules worked, so it survived a full round of testing.
type DeviceUiView = {
  status: "ready" | "offline" | "absent" | "error"
  manifest: string | null
  detail: string | null
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}

/// Subscribe a component to one device's module store.
function useStore(store: DeviceModules): DeviceModules {
  useSyncExternalStore(store.subscribe, store.getVersion)
  return store
}

// ── Reading the manifest ──────────────────────────────────────────────────────

/**
 * One device's modules: the manifest, its status, and the nav it declares.
 *
 * Runs the read when the device comes online and forgets everything when it goes.
 * Not kept across a reconnect — see `forget` in the registry: the board may have been
 * reflashed while it was away.
 */
export function useDeviceModules(device: Device | null): {
  status: ManifestStatus
  detail: string
  nav: DeviceNavItem[]
} {
  const { invoke, state } = useRelayContext()
  // A stable store even when there is no device, so the hook order never changes.
  const store = useStore(forDevice(device?.deviceId ?? ""))

  const deviceId = device?.deviceId ?? null
  const online = device?.connection === "online"

  useEffect(() => {
    if (!deviceId) return

    if (!online) {
      forDevice(deviceId).setStatus(
        "offline",
        "This device is not connected, so there is no pipe to read its manifest over.",
      )
      return
    }

    // The hub has to be up to ask anything. Left in "loading" rather than reported:
    // the topbar already says the relay is down, and saying it twice in two
    // vocabularies is worse than saying it once.
    if (state !== "connected") return

    let cancelled = false

    invoke<DeviceUiView>("GetDeviceUi", deviceId)
      .then((view) => {
        if (cancelled) return
        const target = forDevice(deviceId)

        if (view.status === "absent") {
          target.setStatus(
            "absent",
            "This firmware declares no UI modules, so the relay has nothing to compose.",
          )
          return
        }
        if (view.status === "offline") {
          target.setStatus("offline", view.detail ?? "device is not connected")
          return
        }
        if (view.status === "error" || !view.manifest) {
          target.setStatus("error", view.detail ?? "the device did not answer")
          return
        }

        let manifest: UiManifest
        try {
          manifest = JSON.parse(view.manifest) as UiManifest
        } catch (error) {
          target.setStatus("error", `unparseable manifest: ${errorMessage(error)}`)
          return
        }

        const range = manifest?.hostApi
        if (!range || typeof range.min !== "number" || typeof range.max !== "number") {
          // Answered, but not with a manifest. Treated as "no modules" rather than as
          // an error, because that is what it means for a shell.
          target.setStatus("absent", "the device's reply carried no hostApi range")
          return
        }
        if (HOST_API < range.min || HOST_API > range.max) {
          target.setStatus(
            "unsupported",
            `This device's UI needs host API ${range.min}–${range.max}; this relay speaks ${HOST_API}.`,
          )
          return
        }

        target.setManifest(manifest)
      })
      .catch((error) => {
        if (!cancelled) forDevice(deviceId).setStatus("error", errorMessage(error))
      })

    return () => {
      cancelled = true
    }
  }, [deviceId, online, state, invoke])

  // Forget on the way out, so re-selecting a device re-reads rather than showing what
  // it said the last time somebody looked.
  useEffect(() => {
    if (!deviceId) return
    return () => forget(deviceId)
  }, [deviceId])

  return {
    status: store.status,
    detail: store.detail,
    nav: store.declaredPages().map((page) => ({
      id: page.id,
      label: page.title,
      icon: page.icon,
      moduleId: page.moduleId,
    })),
  }
}

// ── Activating a bundle ───────────────────────────────────────────────────────

/// Where a device's bundle lives from this page's point of view. The manifest gives an
/// absolute device path (`/modules/led.js`); the relay serves that device's files under
/// its own prefix, and the fetch becomes a `web read` on the far side.
function bundleUrl(deviceId: string, entry: string): string {
  return `/devices/${encodeURIComponent(deviceId)}/${entry.replace(/^\//, "")}`
}

function buildShell(
  store: DeviceModules,
  mod: ManifestModule,
  device: Device,
  hub: TransportHub,
): ShellProvider {
  const label = device.name || device.deviceId

  return {
    hostApi: HOST_API,
    device: { id: device.deviceId, name: label },
    transport: deviceTransport(hub, device.deviceId),
    routes: {
      register(page) {
        // Registered but not declared is ignored, because honouring it would make
        // navigation depend on running module code — the property the manifest exists
        // to protect.
        if (!mod.pages.some((declared) => declared.id === page.id)) {
          console.warn(
            `[modules] ${device.deviceId}: "${mod.id}" registered page "${page.id}", ` +
              "which its manifest does not declare — ignored",
          )
          return
        }
        store.registerPage(page)
      },
    },
    ui: {
      notify(message, kind) {
        // Prefixed with the device, because this shell shows many and a bare toast
        // from a module would not say which board it came from.
        const text = `${label}: ${message}`
        if (kind === "error") toast.error(text)
        else if (kind === "success") toast.success(text)
        else toast(text)
      },
    },
  }
}

function activate(
  store: DeviceModules,
  mod: ManifestModule,
  device: Device,
  hub: TransportHub,
): Promise<void> {
  const existing = store.inFlight.get(mod.id)
  if (existing) return existing

  const url = bundleUrl(device.deviceId, mod.entry)

  const started = (async () => {
    const bundle = (await import(/* @vite-ignore */ url)) as {
      activate?: (shell: ShellProvider) => void
    }
    if (typeof bundle.activate !== "function")
      throw new Error(`${mod.entry} has no activate() export`)
    bundle.activate(buildShell(store, mod, device, hub))
    store.markActivated(mod.id)
  })().catch((error) => {
    store.markFailed(mod.id, errorMessage(error))
    // Left in the map: retrying on every render would hammer a 404, and through a
    // device's pipe a retry loop is not free for anyone else on it.
  })

  store.inFlight.set(mod.id, started)
  return started
}

// ── Where to land ────────────────────────────────────────────────────────────

/// The page to open when none is named: the first one this device's manifest declares.
///
/// Declaration ORDER decides it, so the firmware chooses. A product's main feature is
/// declared first and is therefore where you arrive — this shell has no page of its
/// own to fall back to, which is the point.
export function useLandingPage(device: Device | null): string | null {
  const store = useStore(forDevice(device?.deviceId ?? ""))
  return store.declaredPages()[0]?.id ?? null
}

// ── Rendering one page ────────────────────────────────────────────────────────

/// Renders a device's module page, importing its bundle on first use.
export function ModulePageView({
  device,
  pageId,
}: {
  device: Device
  pageId: string
}) {
  const hub = useRelayContext()
  const store = useStore(forDevice(device.deviceId))
  const mod = store.findDeclaringModule(pageId)

  const start = useCallback(() => {
    if (!mod) return
    if (store.activated.has(mod.id) || store.failed.has(mod.id)) return
    void activate(store, mod, device, hub)
  }, [store, mod, device, hub])

  useEffect(() => {
    start()
  }, [start])

  if (store.status === "loading")
    return <p className="text-muted-foreground p-4 text-sm">Loading…</p>

  if (!mod)
    return (
      <Notice
        title="No such module page"
        body={
          <>
            This device does not declare a page called{" "}
            <code className="font-mono text-xs">{pageId}</code>.
          </>
        }
      />
    )

  const failure = store.failed.get(mod.id)
  if (failure)
    return (
      <Notice
        title={`"${mod.id}" could not be loaded`}
        body={
          <>
            {failure}
            <br />
            <span className="text-muted-foreground">
              The firmware declared this page, so the bundle at{" "}
              <code className="font-mono text-xs">{mod.entry}</code> is missing or
              broken — or the pipe to this device dropped while it was being fetched.
            </span>
          </>
        }
      />
    )

  const page = store.pages.get(pageId)
  if (!page) {
    // Declared but not registered. The nav entry stays and says so, because hiding it
    // would make a firmware/module mismatch undiagnosable.
    if (!store.activated.has(mod.id))
      return <p className="text-muted-foreground p-4 text-sm">Loading…</p>
    return (
      <Notice
        title="This module did not provide that page"
        body={
          <>
            <code className="font-mono text-xs">{mod.id}</code> loaded, but registered
            no page <code className="font-mono text-xs">{pageId}</code> — the
            firmware's manifest and its bundle disagree.
          </>
        }
      />
    )
  }

  return <>{page.render() as ReactNode}</>
}

function Notice({ title, body }: { title: string; body: ReactNode }) {
  return (
    <div className="p-4">
      <div className="bg-card text-card-foreground max-w-2xl rounded-xl border p-6 shadow-sm">
        <h2 className="mb-2 text-base font-semibold">{title}</h2>
        <p className="text-sm">{body}</p>
      </div>
    </div>
  )
}
