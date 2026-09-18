# Strux relay

Makes a [Strux](https://github.com/vanBassum/Strux) device reachable from outside
its LAN. Devices dial **out** to it, so there is no port forward and no VPN.

```
device  ──ws──►  /device?id=<id>&fw=<ver>     outbound, NAT-friendly
browser ──ws──►  /hub                         the dashboard's own API (SignalR)
browser ──ws──►  /devices/<id>/ws             relayed onto the device pipe
browser ──http─►  /devices/<id>/{path}        → `web read`, served from cache
agent   ──http─►  /mcp                         three generic tools, bearer token
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

## What a device says about itself

The connect URL carries IDENTITY and nothing else:

```
wss://relay/device?id=<device-id>
X-Strux-Token: <token>
```

`id` is the only field the token proves and the only one anything is keyed on.
Everything a human reads — name, project, firmware version, the commit it was built
from — arrives a chunk later, on the socket, as a **hello**: a flat map of string keys
to string values, all optional, no fixed schema.

```json
{ "type": "relay hello",
  "fw": "0.1.0", "commit": "c538fc6", "project": "DPS50xx",
  "name": "Bench supply", "desc": "Bench power supply, 0-50 V / 0-5 A",
  "idf": "6.0.0", "built": "2026-09-15T10:22:00Z" }
```

It rides its own reserved session (`0xFFFE`, beside telemetry's `0xFFFF`), so the relay
dispatches on the header as it already does and the device needs no new protocol verb —
nothing replies to a hello.

The relay **stores what it gets, shows what it understands, and ignores the rest.** Four
keys have columns (`name`, `project`, `fw`, `commit`); the whole map is kept as JSON
beside them, so teaching the dashboard one more field is a frontend change and not a
migration. A newer device reporting a key this relay never heard of costs nothing, and
an older device omitting one is a blank cell rather than a failed connect.

Why it left the URL: every new fact was a new query parameter, and each one cost
percent-encoding, a slice of a fixed buffer on the device and a change on both sides —
for display data riding in the one part of a connection that is logged, proxied and
cached.

**A pending device says nothing.** It is refused before the upgrade, so there is no
socket for a hello, and its row shows the id, the address and the attempt count. That is
not a gap: the alternative is unauthenticated strings from a device nobody has vouched
for yet, displayed beside an Approve button. What the relay can stand behind is what the
decision gets made on.

**Transition.** The old `?fw=&name=&project=` are still read when they are there, so a
board in the field that has not been reflashed keeps filling in a device list. A device
that sends a hello leaves them off entirely, and the hello wins. The fallback goes once
the fleet has moved.

## MCP: an agent drives a device

The relay speaks [MCP](https://modelcontextprotocol.io) at **`/mcp`** (streamable HTTP,
stateless). Point an MCP client at it, with the bearer token the deployment was given:

```jsonc
{
  "mcpServers": {
    "strux": {
      "type": "http",
      "url": "https://relay.example/mcp",
      "headers": { "Authorization": "Bearer <the relay's MCP token>" }
    }
  }
}
```

Three tools, and deliberately only three:

| tool | what it does |
| --- | --- |
| `devices` | the devices that are approved, exposed and connected right now — id, name, project, firmware, and the one-line description the firmware reports |
| `describe` | one device's own instructions plus every command it has, each with a description and its full argument list (name, type, required, meaning) |
| `execute` | run any of those commands with arguments, and return the device's reply verbatim |

**There is no tool per device command, and that is the design.** A Strux device already
describes itself — `help describe` returns its whole command registry, arguments
included, generated from the handlers themselves rather than from a table somebody
maintains — so discovery is progressive and lives on the device. A relay that published
a tool per command would hold a copy of every device's surface, regenerated every time
somebody flashes a board, and would know what a labelwriter is. It does not, and nothing
here needs changing when a firmware grows a command or when a new kind of product
appears.

The device's part of the bargain: a command carries a one-line description and describes
each argument; the product carries a short description (in the hello) and free-form
instructions (served by `system describe`) explaining how it is meant to be driven. All
of it ships with the firmware, so what an agent reads always matches the build that is
answering.

**Exposure is per device and off by default.** The relay is the trust boundary, so a
board cannot volunteer itself: an operator flips *Expose to MCP* on the device's row in
the dashboard, and the row then shows an MCP marker. A device that is not exposed does
not appear in `devices` and is refused by name to anything that asks for it directly —
the same message as an id that does not exist, because whether an id exists is not
something an MCP client has been given the right to find out.

One boolean, and no more: no read/write/destructive tiers, no per-command rules, no
policy engine. The relay does not know what any command means, so any tier it invented
would be a guess about somebody else's firmware. What a command does to a device is the
device's own description to give, and whether a model may drive that board at all is the
operator's switch.

### Getting in: bearer tokens

`/mcp` is the one path that does **not** sit behind the reverse proxy's forward-auth,
and it cannot: forward-auth answers an unauthenticated request with a redirect to a
login flow, and a program following a 302 to an OAuth screen learns nothing. So the
proxy routes `/mcp` straight through and the relay checks an
`Authorization: Bearer <token>` header itself — in the process that owns the thing being
protected, rather than in a label on a container.

**Tokens are managed on the dashboard's MCP page.** Name one after whatever will hold
it, and it is shown exactly once: the relay stores a SHA-256 hash and a four-character
hint, so nothing on this side can produce the value again. Each row says when it was
created and when it was last used — which is how "is this one still in something's
config" gets an answer — and revoking takes effect on the very next request, because
every request is checked rather than a session being established.

That page also carries two copy-paste blocks: the MCP client config, and a plain-text
briefing to hand an assistant that says where to connect, what the three tools are, and
the two things that stop an agent guessing wrong. Both come out with a fresh token
already filled in.

```jsonc
{
  "mcpServers": {
    "strux": {
      "type": "http",
      "url": "https://relay.example/mcp",
      "headers": { "Authorization": "Bearer <the token the page showed you>" }
    }
  }
}
```

```bash
curl -i https://relay.example/mcp \
  -H "Authorization: Bearer $STRUX_MCP_TOKEN" \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
```

**A second source, deliberately.** The deployment may also configure one token:

```
Relay__Mcp__Token=<a long random string>      # env var, from your secrets store
```

It works exactly like an issued token and the page shows that it exists, but it cannot
be revoked from the dashboard — it is rotated where every other secret is. It earns its
place by being the credential that works *before* anyone has opened the dashboard, on a
relay whose database has no tokens in it yet. Leave it out and the page is the only
source, which is the ordinary case once a relay is running.

What the check does, and deliberately does not do:

* every request under `/mcp` is checked, not just the first — streamable HTTP is a POST
  for the call, a GET that holds the event stream open, and sub-paths (`/mcp/sse`,
  `/mcp/message`) beneath the same prefix. Authenticating only the opening request would
  leave the stream that carries the answers open to whoever asked for it;
* a missing, wrong or revoked token is **401** with `WWW-Authenticate: Bearer`, never a
  redirect;
* the deployment token is compared in constant time; issued tokens are looked up by
  hash, where that question does not arise — matching a hash needs a preimage, not
  patience;
* **no credential of any kind means the endpoint refuses everything.** An unset secret
  must not silently publish a device-driving API; the MCP page says so in as many words
  when that is the state;
* there are no users, scopes, refresh tokens or OAuth behind any of it. A token answers
  one question — "is this a machine we gave a credential to". Which devices that machine
  may then reach is the *other* gate, and stays the per-device MCP switch.

The dashboard, the hub and every browser path keep their forward-auth, and `/device`
keeps its own unauthenticated route and its own device token. Neither changed.

## A device opens its own UI

Clicking a device in the list opens **that device's own site**, at `/devices/<id>/`,
in a new tab. There is no page in between: the row is the link.

It was in between until now. This shell had a device route of its own that read the
firmware's `ui modules` manifest, loaded the bundles it named and drew them in this
sidebar — so reaching a device cost two clicks, the second of them on a page whose
whole content was a way off itself. The device already serves a complete UI, and the
relay already proxies every byte of it over the same pipe, so composing a second one
here bought nothing and cost a hop.

What that means for a device's pages: **nothing is lost.** A firmware module's page
is still there, drawn by the device's own shell, which loads its bundles through this
relay's file route like every other asset — including the cache warmer's head start on
them (see `ModuleBundlesAsync`, which asks `ui modules` for exactly that reason and
stays).

A row is only clickable when the device is **online and approved**: a pending device
has no pipe, an offline one has no pipe to fetch its page over, and a row that clicks
through to nothing is worse than one that does not click. The kebab menu carries the
same action disabled, with the reason in its tooltip, which is where a row that does
nothing gets explained.

### The server half went with it

`GetDeviceUi`, `DeviceCommand`, `SubscribeDeviceLogs`/`UnsubscribeDeviceLogs`, the
`upload`/`download` routes and `Models/Ui.cs` are gone, and so is the per-device log
hub group and `DeviceConnection`'s upload/download session helpers — every one of them
existed to serve a module running in this shell. The hub is back to what it was for:
the device list, the cache, telemetry and pairing. It knows the name of no device
command at all, which was always the point and is now true because there is nothing
left to break it.

What stays is everything a device's OWN page needs: the pipe at `/device`, the browser
end at `/devices/<id>/ws`, and the file proxy with its cache. The warmer's
`ModuleBundlesAsync` went too, once Strux deleted `ui modules` from the firmware: it
asked every device on connect which bundles to pre-fetch, and the answer became a
refusal every time. The warmer now pre-fetches what `index.html` actually references,
which is what it did before modules existed.

## Not built yet

* **Device-side flash progress.** The browser sees its own upload progress, which
  tracks closely because the relay forwards chunk by chunk, but the device's own write
  position is not surfaced — that would need a hub group per upload.

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
