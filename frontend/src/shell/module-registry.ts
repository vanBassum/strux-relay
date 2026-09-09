// What this shell knows about one device's UI modules: the manifest that device sent,
// and the contributions its bundles registered when they were activated.
//
// ── Per device, and that is the whole difference from the device shell ─────────
// Strux's own shell keeps this in module scope, because there is exactly one device
// on the other end of its socket and a singleton cannot be wrong. This shell is
// inherently multi-device: two boards can both declare a page with id "led", drawn by
// two different bundles built from two different firmwares. One store would let one
// device's registration answer for another — a page silently rendering the wrong
// board's UI, which is worse than an error.
//
// A plain store with subscribers rather than React state, because modules register
// imperatively from `activate(shell)` — a call that happens inside a dynamic import,
// not inside a render — and because the sidebar and the page are two trees that both
// have to see the result.
//
// Nothing here imports a module or knows a module's shape. It holds ids and thunks.

import type { ManifestModule, ModuleCard, ModulePage, UiManifest } from "@shell/contract"

/// Where the shell is in the process of learning what a device offers.
///
/// The three not-ready outcomes are deliberately separate, because they call for
/// three different things on screen. `absent` is firmware with no `ui modules`
/// command — the mixed-fleet case, the common one, and NOT a fault: the device's own
/// page is the answer. `unsupported` is firmware that wants a newer shell, and the
/// rest of the dashboard stays fully usable. `error` is a device that should have
/// answered and did not, which is the only one worth showing as a problem.
export type ManifestStatus =
  | "loading"
  | "ready"
  | "absent"
  | "unsupported"
  | "offline"
  | "error"

/// One device's modules. Created on demand, dropped when its device goes.
export class DeviceModules {
  status: ManifestStatus = "loading"
  manifest: UiManifest | null = null
  /// Shown when the status is anything but "ready" or "loading".
  detail = ""

  /// Module ids whose bundle failed to import — a 404, a syntax error, a throwing
  /// activate. Kept so a page can say which one rather than rendering nothing.
  readonly failed = new Map<string, string>()
  readonly activated = new Set<string>()
  readonly pages = new Map<string, ModulePage>()
  readonly cards = new Map<string, ModuleCard>()

  /// One import per module, deduplicated so a card and a page from the same bundle do
  /// not fetch it twice. On this store rather than in module scope: the URL is
  /// per-device, so two devices running the same module are two separate ES module
  /// instances — which is required, not incidental. Each gets its own `activate`.
  readonly inFlight = new Map<string, Promise<void>>()

  // useSyncExternalStore wants a stable snapshot, and a mutable object is not one. A
  // version counter is: it changes exactly when something a caller can see changed.
  private version = 0
  private readonly listeners = new Set<() => void>()

  /// Only ever put in a warning: which board a module misbehaved on.
  readonly deviceId: string

  constructor(deviceId: string) {
    this.deviceId = deviceId
  }

  subscribe = (fn: () => void): (() => void) => {
    this.listeners.add(fn)
    return () => {
      this.listeners.delete(fn)
    }
  }

  getVersion = (): number => this.version

  changed() {
    this.version++
    for (const fn of this.listeners) fn()
  }

  setManifest(manifest: UiManifest) {
    this.manifest = manifest
    this.status = "ready"
    this.detail = ""
    this.changed()
  }

  setStatus(status: ManifestStatus, detail = "") {
    this.manifest = null
    this.status = status
    this.detail = detail
    this.changed()
  }

  // ── What modules register ───────────────────────────────────────────────────
  // Ids are matched against the manifest by the caller (ModuleHost), not here: this
  // records what was offered, and the matching rules are policy.

  registerPage(page: ModulePage) {
    if (this.pages.has(page.id))
      console.warn(
        `[modules] ${this.deviceId}: page "${page.id}" registered twice; last one wins`,
      )
    this.pages.set(page.id, page)
    this.changed()
  }

  registerCard(card: ModuleCard) {
    if (this.cards.has(card.id))
      console.warn(
        `[modules] ${this.deviceId}: card "${card.id}" registered twice; last one wins`,
      )
    this.cards.set(card.id, card)
    this.changed()
  }

  markActivated(moduleId: string) {
    this.activated.add(moduleId)
    this.changed()
  }

  markFailed(moduleId: string, reason: string) {
    this.failed.set(moduleId, reason)
    this.changed()
  }

  // ── Reading the manifest, before any module code has run ────────────────────

  /// The manifest's declared pages, in declaration order. Drawn before any bundle has
  /// been fetched — that is the whole reason the manifest exists — so this must not
  /// consult `pages` at all.
  declaredPages(): { moduleId: string; id: string; title: string; icon: string }[] {
    if (!this.manifest) return []
    return this.manifest.modules.flatMap((mod) =>
      mod.pages.map((page) => ({
        moduleId: mod.id,
        id: page.id,
        title: page.title,
        icon: page.icon,
      })),
    )
  }

  declaredCardIds(): { moduleId: string; id: string }[] {
    if (!this.manifest) return []
    return this.manifest.modules.flatMap((mod) =>
      mod.cards.map((id) => ({ moduleId: mod.id, id })),
    )
  }

  findDeclaringModule(pageId: string): ManifestModule | null {
    if (!this.manifest) return null
    return (
      this.manifest.modules.find((mod) => mod.pages.some((page) => page.id === pageId)) ??
      null
    )
  }
}

const byDevice = new Map<string, DeviceModules>()

/// This device's store, created on first ask. Stable across renders, so a component
/// can subscribe to it.
export function forDevice(deviceId: string): DeviceModules {
  let store = byDevice.get(deviceId)
  if (!store) {
    store = new DeviceModules(deviceId)
    byDevice.set(deviceId, store)
  }
  return store
}

/// Forget a device entirely, so its next manifest read starts clean.
///
/// Called when a device's pipe drops. The manifest is not kept across a reconnect on
/// purpose: the device may have been reflashed while it was away, and a nav drawn from
/// the old manifest would be a sidebar of pages that then fail. The relay's own file
/// cache makes the same choice for the same reason.
///
/// Already-imported bundles are NOT unloaded — a browser cannot unload an ES module.
/// A reconnected device re-imports from the same URL and gets the cached instance,
/// which is correct while the firmware is the same and is why `activate` has to be
/// idempotent from the module's side. A reflash changes the bytes but not the URL, so
/// seeing new module code needs a page reload; that is a known cost of stable,
/// unhashed bundle names, which the firmware needs in order to name its own entry.
export function forget(deviceId: string) {
  byDevice.delete(deviceId)
}
