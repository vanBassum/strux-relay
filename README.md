# Strux relay

Makes a [Strux](https://github.com/vanBassum/Strux) device reachable from outside
its LAN. Devices dial **out** to it, so there is no port forward and no VPN.

```
device  ──ws──►  /device?id=<id>&fw=<ver>     outbound, NAT-friendly
browser ──ws──►  /hub                         the dashboard's own API (SignalR)
browser ──ws──►  /devices/<id>/ws             relayed onto the device pipe
browser ──http─►  /devices/<id>/{path}        → `web read`, served from cache
```

ASP.NET Core on .NET 10, with a React + shadcn/ui dashboard. It replaced a
~1400-line Python server; that version is in the history if you need it
(`git log -- relay.py`).

## Run it

```bash
cd api && dotnet run                 # http://0.0.0.0:8080, dashboard included
```

It binds **every** interface, not just loopback, because a device dials in from the
LAN — `applicationUrl: http://localhost:8080` leaves nothing listening on the address
`relay.url` points at, and the device's SYN then has no socket to land on. On Windows
that reads as a *timeout* rather than a refusal (the firewall drops inbound packets to
a port with no listening socket instead of answering RST), so it looks exactly like the
firewall problem below even when the rule is already in place.

For UI work, run the dev server too and use that instead — it proxies `/hub` and
`/device*` through to Kestrel:

```bash
cd frontend && pnpm dev          # prints its own URL; 5173 unless taken
```

`pnpm build` writes into `api/wwwroot`, which the API serves as static files, so
a plain `dotnet run` is enough to see the built dashboard.

On Windows, inbound connections to a fresh port are blocked by default, so a
device's connect attempt times out with `transport_ws: Error connecting to host`.
Allow the port once (elevated):

```powershell
New-NetFirewallRule -DisplayName "Strux relay" -Direction Inbound `
  -Protocol TCP -LocalPort 8080 -Action Allow -Profile Private
```

## Point a device at it

Set two settings on the device (settings UI, or `setSetting` + `saveSettings`
over its WebSocket) and reboot:

| setting          | value                                |
| ---------------- | ------------------------------------ |
| `relay.url`      | `ws://<server-ip>:8080/device`        |
| `relay.enabled`  | `true`                                |
| `relay.deviceId` | optional — defaults to `esp32-<mac>`  |

The dashboard shows that URL with a copy button, derived from wherever the page
was served, so behind a reverse proxy it is already the right one.

A new device is **refused once**: it appears in the device list as pending, and
the retry after you approve it succeeds. Nothing is ever pushed to a device —
that is the whole handshake.

## Two databases, one model

The pairing store is Entity Framework Core, and the provider is configuration:

```jsonc
"Relay": { "Database": { "Provider": "sqlite", "ConnectionString": "Data Source=relay.sqlite" } }
```

SQLite today. The model is deliberately provider-neutral — no raw SQL, no
provider-specific defaults, UTC `DateTime` rather than `DateTimeOffset` (which
SQLite refuses to `ORDER BY`) — so adding PostgreSQL is one arm of the switch in
`RelayDatabase` plus its own migrations folder, since migrations are
provider-specific.

What a second provider does *not* buy is a second relay instance: a device's
socket lives in the process that accepted it, so two instances behind a load
balancer cannot reach each other's devices. That needs routing by device id, not
a different database.

## How it stays dumb

The relay **never parses a session payload**. Commands, the auth handshake,
uploads and log lines are opaque bytes moved between two sockets. It does parse
and rewrite the 3-byte session header, because it must own the session-id space:

* Browser ids and the relay's own `web read` ids would both start at 1 on the same
  device socket and collide, presenting as "the device replied to the wrong
  request". So browser ids are **rewritten** into the low half (1–0x7FFF) and the
  relay's own sessions come from the high half (0x8000+).
* Session 0 is the device's log broadcast, so the pipe is **not**
  request/response: session-0 chunks belong to every attached browser rather than
  to whoever asked. Session 0xFFFF is telemetry, consumed rather than forwarded.
  Session-0 chunks go to every attached browser pipe; telemetry goes to the sink.
* One request is in flight per device, held for a whole session. The device
  dispatches a chunk synchronously with no slot table, so overlapping sessions
  would let a file fetch's chunk land inside a streamed request body. A watchdog
  releases the pipe after silence for that session — **not** after a fixed session
  length, which used to take the pipe away from a healthy multi-second upload and
  let a page load interleave into its body, killing both.

The device owns its frontend storage: the relay asks for `/index.html` and never
learns it lives gzipped on a FAT partition called `www`. `Content-Encoding` comes
back from the device, so gzip can pass straight through — which is what the file
proxy will do when it lands.

Who may connect: a device must be approved, and must present the token it was
approved with in an `X-Strux-Token` header, or the upgrade is refused with a 403
*before* it is accepted — so the device logs a status rather than a socket that
opens and dies. The device generates its own token; the relay only ever pins the
value a device presented.

The human side is expected to sit behind a reverse proxy that authenticates
users; there are no accounts here. `/device` cannot be, because a device cannot
follow a login redirect — hence the token, and hence `/device` being its own path
rather than the base URL.

## Telemetry

Devices emit InfluxDB **line protocol** on session `0xFFFF`, already formatted —
one line per chunk. The relay forwards those bytes untouched; it reads a line
only to fill the diagnostics table, never to rewrite one.

Where it goes is configuration, and InfluxDB is the first sink rather than the
only shape allowed:

```jsonc
"Relay": {
  "Telemetry": {
    "QueueCapacity": 10000,        // bounded — a sink that is down costs a gap, not memory
    "BatchSize": 500,
    "MaxBatchAge": "00:00:05",
    "Influx": { "Url": "", "Token": "", "Org": "", "Bucket": "", "Timeout": "00:00:10" }
  }
}
```

In a container that is `Relay__Telemetry__Influx__Url` and friends. **Note the
rename:** the Python relay read `INFLUX_URL` / `INFLUX_TOKEN` / `INFLUX_ORG` /
`INFLUX_BUCKET`, and those names are not read any more — a compose file carrying
them will leave telemetry unconfigured rather than fail.

Adding a second destination is a class beside `InfluxTelemetrySink` and one line
in `Program.cs`: `TelemetryRouter` handles queueing, batching, counters and the
live feed, and knows no sink-specific vocabulary.

**The relay is not a telemetry store.** Counters are in memory and reset on
restart, nothing is persisted, and a point that cannot be forwarded is dropped
rather than retried. The Telemetry page stays useful with no sink configured:
points still arrive, are counted and shown live, and are counted as dropped for
"no sink configured" — which is what lets the device→relay half be verified on
its own. The live table lives in the browser and goes on refresh.

A device sends nothing until `telem.enabled` is set, and that setting is read
once at boot — `TelemetryManager::Init` returns early when it is off — so
enabling it needs a reboot, not just `settings save`.

## The frontend cache

A device's own frontend is served through the relay at `/devices/<id>/…`, from a
cache keyed on `(deviceId, path)`.

**A connection is the cache's lifetime.** Entries are dropped when a device
connects and kept for as long as that connection lasts — no TTL and no
revalidation, because the only moment a device's content can change under us is
one we already see: it has to reboot, and rebooting drops the pipe. The one case
connect cannot see is `www` replaced on a running device; Clear is the answer.

Fresh connections are **warmed** in the background: index.html, then the assets
it names. Fetching what it *names* rather than crawling the partition keeps this
to the files actually served, and stays correct across a rebuild for free — a new
build names new files. Warming goes through the same fetch a page load uses, so a
browser arriving mid-warm shares the in-flight fetch instead of starting a second.

The immutable/mutable split is about what the **browser** is told, not about
server-side lifetime: a vite content-hashed `/assets/name-<hash>.js` gets
`immutable` for a year, so a second page load costs no pipe traffic at all, while
index.html gets `no-cache` and usually a 304 off the ETag.

```jsonc
"Relay": { "Cache": { "MaxBytes": 33554432, "WarmOnConnect": true } }
```

Bytes rather than entries, because a frontend is one small index.html and one
large bundle and it is the bundle that decides whether this fits in a container.
Eviction is least-recently-used. The Cache page shows the real policy, per-device
size/files/last-warmed/last-used, and Warm and Clear per device or for everything.

## Device UI modules

A device's firmware declares its own UI and ships the bundle that draws it, so this
relay composes a device page without knowing anything about that device at build
time. Two pieces on the server:

* **`GetDeviceUi`** reads the device's manifest (the `ui modules` command). A
  dedicated hub method rather than a generic command call, because the
  classification is the interesting part and only the relay can make it: a device
  that *refused* the command ships no modules — the mixed-fleet case, and the common
  one — while silence is a fault. Through a generic call both arrive as "it threw".
* **`DeviceCommand`** is how a module talks to its device, over the pipe the device
  already dialled and through the same gate as a file read. Not a second transport:
  a browser speaking the pipe itself would need a socket per device with its own
  reconnect and lifecycle, reimplementing `DeviceConnection` in a browser.

Failure comes back as a **result, not an exception**. SignalR does not deliver a
HubException's message intact — it wraps it in "An unexpected error occurred
invoking 'X' on the server." — and a module is promised the device's own words.

The cache warmer asks `ui modules` and warms the bundles it names, because a bundle
is named by firmware in a command reply and no regex over `index.html` can find it.

The shell contract is vendored from Strux byte-identical with a lock file, and
`pnpm build` fails if the copy was edited or the lock does not describe it. Upstream
being ahead is a warning: this shell may deliberately speak an older `hostApi` and
tells a device so through the manifest's range.

## Not built yet

* **Console, Settings and Firmware for a device.** The relay shell gives a device an
  *Overview* — its contributed cards — and nothing else, while the device's own shell
  has had all three for a long time. They are **not** modules and must not become
  modules: they are framework features every Strux device has, and `settings list`
  already describes itself. Settings needs nothing new here; a live Console needs a
  per-device hub group, because session-0 broadcasts reach browser pipes and not hub
  clients; Firmware needs a streaming sibling to `CommandAsync`, because an upload is
  a session and not one envelope plus one reply. Until then *Open device UI* serves
  the device's own page over the same pipe, so nothing is unreachable.

Also missing, and never present: TLS (the proxy's job), and any way to block a
device for good — rejecting only forgets it, and a refused device keeps retrying.

## Container

Built by GitHub Actions and pushed to GHCR. A push to `main` moves `main` and
`sha-<short>`; **`latest` moves only for a `v*` tag**, so a deployment tracking
`latest` does not pick up whatever last landed on main.

```bash
docker run -p 8080:8080 -v relay-data:/app/data ghcr.io/vanbassum/strux-relay:main
```

Approvals live in `/app/data`, so mount it or re-pair every device after an
update.
