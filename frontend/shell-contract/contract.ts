// The shell contract: everything a UI module may assume about its host.
//
// SOURCE OF TRUTH. This file is vendored into the relay (which has its own React,
// its own primitives and its own build) and compared against this copy in CI. That
// is why it has NO IMPORTS, not even type-only ones: a file that imports cannot be
// copied into a repo whose module graph it knows nothing about.
//
// It deliberately does NOT describe the wire. The session-chunk framing, the auth
// handshake, the reconnect loop and the single-in-flight queue all live in the
// shell's own client; a module gets `request` and nothing beneath it. A module that
// framed its own chunk would be a second transport, which is the thing this design
// exists to avoid.
//
// The one runtime value here is `HOST_API`, and it belongs here rather than in each
// shell: this file's *shape* is what version 1 means, so a shell that vendored the
// contract and then declared its own number could disagree with the shape it is
// implementing. One file, one version.

/// Bumped when the host contract changes in a way a module can notice — the shape
/// below AND the runtime it is handed (React's major version included). One integer,
/// because a module cannot usefully negotiate per-dependency: it either runs against
/// this host or it does not.
///
/// The manifest (`ui modules`) answers with the range the *firmware* was built
/// against; the shell answers with its own. Disjoint ranges mean "needs a newer
/// shell", which is a message, not a crash.
export type HostApiVersion = number;

/// Which device a module is talking to. Present so a page can title itself and so
/// the multi-device relay shell can render the same module twice without the two
/// copies confusing each other. Not a capability: identity, not a handle.
export interface DeviceIdentity {
  /// Stable id. On the device shell this is the device's own name; through the
  /// relay it is the id the relay knows it by. A module must not parse it.
  readonly id: string;
  /// Display name, already resolved by the shell.
  readonly name: string;
}

/// The one way a module reaches its device: a command, by name, with arguments.
///
/// `command` is the same two-word route the wire uses ("led get", "settings list"),
/// because there is exactly one command surface and no module-private dialect. Any
/// command in the device's table is reachable — this is not a permission boundary
/// and could not be one, since modules run in-process with the shell.
export interface DeviceTransport {
  /// Resolves with the parsed reply. Rejects with an `Error` whose message is the
  /// device's own reason when the device refuses the request (FLAG_REJECT, which is
  /// also how a handler's RequestError arrives), `Error("Request timeout")` when the
  /// reply goes idle, and one rejection per pending request when the connection
  /// closes.
  ///
  /// There is no cancellation, so a page MUST tolerate a reply landing after it has
  /// unmounted. Through the relay every call takes that device's request gate, so it
  /// contends with the cache warmer and with other operators: poll slowly, and prefer
  /// one command that answers in one round trip.
  request<T = unknown>(command: string, args?: Record<string, unknown>): Promise<T>;
}

/// A page a module contributes to the shell's navigation.
///
/// `id` must match an id the manifest declared for this module, and the shell is what
/// checks it: a page registered but not declared is ignored (honouring it would make
/// navigation depend on running module code, which is the property the manifest
/// exists to protect), and a page declared but never registered keeps its nav entry
/// and says so when opened (hiding it would make a firmware/module mismatch
/// undiagnosable).
export interface ModulePage {
  readonly id: string;
  /// Rendered into the shell's content area. `unknown` rather than a React type
  /// because this file cannot import React — the shell narrows it, and the module
  /// and shell share one React instance through the import map.
  readonly render: () => unknown;
}

/// A card a module contributes to the shell's dashboard.
export interface ModuleCard {
  readonly id: string;
  readonly render: () => unknown;
}

/// How a module tells the user something. Deliberately tiny: a module owns its own
/// page's content, so the only thing it needs from the shell is the chrome it does
/// not own.
export interface ShellUi {
  notify(message: string, kind?: "info" | "success" | "error"): void;
}

/// What `activate` is handed. The whole host surface, in one object.
///
/// Note what is absent. There is no `subscribe`: the device has exactly two
/// device-initiated sessions — session 0 (log broadcasts) and 0xFFFF (telemetry,
/// addressed to the relay's database and never to a browser) — so there are no
/// topics and no per-feature events for a subscription to carry. Modules poll. When
/// Strux grows a real feature-event concept it is a new session concept plus a
/// fan-out design, and it earns a `hostApi` bump; that is what the integer is for.
export interface ShellProvider {
  readonly hostApi: HostApiVersion;
  readonly device: DeviceIdentity;
  readonly transport: DeviceTransport;
  readonly routes: { register(page: ModulePage): void };
  readonly cards: { register(card: ModuleCard): void };
  readonly ui: ShellUi;
}

/// Every module bundle's single export. Called once, after the bundle is imported,
/// with the shell it is running inside.
export type ActivateFn = (shell: ShellProvider) => void;

// ── The manifest, as `ui modules` answers it ──────────────────────────────────
// Declared here because both shells parse it and the relay's cache warmer reads it
// too. It is a command reply, not a file: nothing here travels over HTTP.

/// One navigable page, as the FIRMWARE declares it — before any module code has run.
/// This is what lets a shell draw a complete sidebar without importing a single
/// bundle.
export interface ManifestPage {
  readonly id: string;
  readonly title: string;
  /// A lucide icon name. Resolved by each shell against its own lucide version, so a
  /// name valid on one shell may not exist on the other: a missing icon MUST fall
  /// back to a default and must never throw. An icon is not worth a blank sidebar.
  readonly icon: string;
}

export interface ManifestModule {
  readonly id: string;
  /// Absolute path to the bundle, served by whoever is serving the page: the
  /// device's own static file server on the LAN, or `/devices/<id>/…` through the
  /// relay, where it becomes a `web read` over the existing pipe. Stable, not
  /// content-hashed, because the firmware has to be able to name its own entry.
  readonly entry: string;
  readonly pages: readonly ManifestPage[];
  /// Dashboard card ids this module provides.
  readonly cards: readonly string[];
}

export interface UiManifest {
  readonly hostApi: { readonly min: HostApiVersion; readonly max: HostApiVersion };
  readonly modules: readonly ManifestModule[];
}

/// The host API version this copy of the contract describes. A shell compares its own
/// against the manifest's range; a mismatch omits navigation and says why.
export const HOST_API: HostApiVersion = 1;
