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

  /// Sends a command whose request has a BODY: an envelope chunk, then `body` streamed
  /// on the same session, then one reply. `partition write` is the reason this exists —
  /// a firmware image is not an argument.
  ///
  /// Separate from `request` because it is a different shape on the wire, not because
  /// it is a different destination. A module still names an ordinary command and still
  /// gets the device's own reply; what changes is that the request does not fit in one
  /// chunk. Anything a handler needs *before* the body still goes in `args`.
  ///
  /// `onProgress` reports a fraction between 0 and 1 and is best-effort: on the device
  /// shell it is the device's own write position, which is the honest number, and
  /// through the relay it is bytes accepted by the relay, which lags the flash write at
  /// the tail. Neither is a guarantee that anything landed — the reply is.
  upload<T = unknown>(
    command: string,
    args: Record<string, unknown> | undefined,
    body: Blob,
    onProgress?: (fraction: number) => void,
  ): Promise<T>;

  /// The mirror of `upload`: a command whose REPLY is a stream rather than a value.
  /// `partition read` is the reason — a flash image is not a JSON field.
  ///
  /// Resolves with the bytes. What the caller does with them is the caller's business:
  /// this deliberately does not save a file, because "hand the user a download" is a
  /// shell concern on one host and a different one on the other, while "give me the
  /// bytes" is the same everywhere.
  ///
  /// `onProgress` is a fraction when the total is known — a partition's size comes
  /// from the partition table, which the module has and the transport does not — and
  /// is not called at all when it is not.
  download(
    command: string,
    args?: Record<string, unknown>,
    total?: number,
    onProgress?: (fraction: number) => void,
  ): Promise<Blob>;

  /// The device's log lines, as they are written. Returns its own unsubscribe.
  ///
  /// The ONE device-initiated stream in the system, and it is here because it exists
  /// rather than because a general subscription mechanism was wanted: the device
  /// broadcasts on session 0 and always has. There is still no `subscribe(topic)` and
  /// no per-feature events — see the note on ShellProvider — so this is spelled out as
  /// what it is instead of being the first user of a framework with one user.
  ///
  /// Lines arrive only while the shell is attached, so a module that wants what came
  /// before it opened must also ask `log list`. Nothing is replayed.
  logs(handler: (line: DeviceLogLine) => void): () => void;
}

/// One broadcast log line, in the device's own shape and passed through: whatever it
/// writes to session 0 arrives here parsed and not reinterpreted. Today that is a
/// single pre-formatted line — `I (1234) Tag: message` — because that is what the
/// device already had to produce for its serial console, so nothing on either side
/// has to agree about a structure neither of them has.
///
/// Every field is optional on purpose. A module reads what it recognises; a firmware
/// that grows a richer broadcast does not break one that does not know about it.
export interface DeviceLogLine {
  readonly log?: string;
  readonly [key: string]: unknown;
}

/// A page a module contributes to the shell's navigation, and the ONLY thing a module
/// contributes. There used to be a second kind — a card, rendered into a home screen
/// the shell owned — and it went when the shell stopped owning any device page at all.
/// A shell adds nothing to a device's navigation, so there was nowhere left for a card
/// to render; two extension points where one will do is worse than the convenience it
/// bought.
///
/// The FIRST page a manifest declares is the landing page. A device's own product
/// belongs on the screen you arrive at, so a firmware that has one main feature
/// declares it first and that is the home screen — decided by the firmware, in
/// declaration order, rather than by a shell picking a favourite.
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

/// How a module tells the user something. Deliberately tiny: a module owns its own
/// page's content, so the only thing it needs from the shell is the chrome it does
/// not own.
export interface ShellUi {
  notify(message: string, kind?: "info" | "success" | "error"): void;
}

/// What `activate` is handed. The whole host surface, in one object.
///
/// Note what is absent, and what is not. There is still no `subscribe(topic)`: the
/// device has exactly two device-initiated sessions — session 0 (log broadcasts) and
/// 0xFFFF (telemetry, addressed to the relay's database and never to a browser) — so
/// there are no topics and no per-feature events for a subscription to carry. Modules
/// poll for feature state. `transport.logs` is the one exception and is named after
/// the one thing it carries for exactly that reason: session 0 exists, so a Console
/// module can have live lines without inventing a subscription framework whose only
/// user would be logs. When Strux grows real per-feature events it is a new session
/// concept plus a fan-out design, and it earns another `hostApi` bump.
///
/// There is also no `cards` any more. A shell owns no page under a device, so nothing
/// hosted them.
export interface ShellProvider {
  readonly hostApi: HostApiVersion;
  readonly device: DeviceIdentity;
  readonly transport: DeviceTransport;
  readonly routes: { register(page: ModulePage): void };
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
}

export interface UiManifest {
  readonly hostApi: { readonly min: HostApiVersion; readonly max: HostApiVersion };
  readonly modules: readonly ManifestModule[];
}

/// The host API version this copy of the contract describes. A shell compares its own
/// against the manifest's range; a mismatch omits navigation and says why.
export const HOST_API: HostApiVersion = 2;
