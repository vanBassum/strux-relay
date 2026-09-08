# Strux relay

Makes a [Strux](https://github.com/vanBassum/Strux) device reachable from outside
its LAN. Devices dial **out** to it, so there is no port forward and no VPN.

```
device  ──ws──►  /device?id=<id>&fw=<ver>     outbound, NAT-friendly
browser ──ws──►  /hub                         the dashboard's own API (SignalR)
browser ──ws──►  /devices/<id>/ws             relayed onto the device pipe  (not ported)
browser ──http─►  /devices/<id>/{path}        → `web read` on the device      (not ported)
```

ASP.NET Core on .NET 10, with a React + shadcn/ui dashboard. It replaced a
~1400-line Python server; that version is in the history if you need it
(`git log -- relay.py`).

## Run it

```bash
cd api && dotnet run                 # http://localhost:8080, dashboard included
```

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
  Neither has anywhere to go yet — see *Not ported yet*.
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

## Not ported yet

Carried over from the Python relay and still missing here:

* **The browser pipe** (`/devices/<id>/ws`) and the **file proxy**
  (`/devices/<id>/{path}`), so a device's own UI cannot be opened through the
  relay. `WebReadAsync` exists but nothing calls it.
* **The file cache**, whose lifetime was the device's connection.
* **Telemetry forwarding.** Session 0xFFFF is recognised and dropped with a
  warning; there is no sink. It should come back as a configurable destination
  rather than InfluxDB only.

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
