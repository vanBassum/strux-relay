# Operating this relay

Facts worth not re-deriving about the deployment at `strux.vanbassum.com`, and the
settled decisions behind it. The [README](../README.md) says what the relay *is*; this
says how it is run and why it is arranged the way it is.

Moved here from the Strux repo's `docs/backlog/`, which was retired in favour of issues
(vanBassum/strux-relay#5). Anything that was true only of the Python relay, or of a
firmware that has since changed, was dropped rather than carried across; where a number
has moved, the current one is here and the old one is not.

## Settled decisions

- **SQLite for state.** Entity Framework Core, provider chosen by configuration, one
  file on the `runtime` volume. The model stays provider-neutral so PostgreSQL is one
  arm of the switch in `RelayDatabase` plus its own migrations folder — see the README.
- **`web.password` stays empty on devices behind this relay.** Authentik guards remote
  access; a device password only matters on the LAN, and setting one costs a login on
  every browser that reaches the device through the relay.
- **Domain is `strux.vanbassum.com`.** `*.vanbassum.com` already resolves to the host,
  so no DNS record was needed.
- **The GHCR package is public.** The source is in a public repo already, so a private
  image would only add a PAT to keep alive on the server.
- **Who may approve a device:** whoever can load the dashboard. The Authentik blueprint
  binds that to `authentik Admins`. The pairing API carries no check of its own, because
  a second one could only disagree with the proxy.
- **`relay.deviceId` stays MAC-derived** (`esp32-<mac>`). The plan once said it should
  become something random, which was right while the id was the only identity. Once the
  token proves the device, the id carries no secrecy — and MAC-derived buys what random
  cannot: the MAC is in efuse, so a wiped board comes back as the *same* id with a fresh
  token and pairing is one re-approve. A random id in NVS would return as a stranger and
  orphan its old approval. The id is technical; `device.name` is what a human reads.
- **WireGuard was considered and rejected**: a network-level fix for an app-level
  problem, with heavy key and routing operations.

## Routing: three routers, and one of them must not be a prefix

`Path`, **not** `PathPrefix`, on the device router: `/devices/<id>/ws` starts with
`/device`, so a prefix rule would quietly put the browser socket on the unauthenticated
route.

| router                  | rule                                             | middleware           |
| ----------------------- | ------------------------------------------------ | -------------------- |
| `strux-relay-device`    | `Host(…) && Path(/device)`                        | none                 |
| `strux-relay-outpost`   | `Host(…) && PathPrefix(/outpost.goauthentik.io/)` | → `authentik@docker` |
| `strux-relay`           | `Host(…)`                                         | `authentik@docker`   |

**`/device` can never sit behind Authentik.** Forward-auth answers an unauthenticated
request with a redirect to the login flow, and the device would log
`upgrade refused with HTTP 302` for ever. The proxy protects the human side only. That
is also why the endpoint carries its own credential — see *Pairing* below.

The same argument now applies to `/mcp`, and to every path in the OAuth flow except the
consent page: a program following a 302 to a login screen learns nothing. The README's
*Getting in* section is the reference for which of those paths stay behind forward-auth
(`/oauth/authorize`, and only that one) and which are routed straight through.

## The stack

- **House conventions:** `stacks/<name>/<name>-compose.yml`, `expose` rather than
  `ports`, external `ingress-network`, per-stack state in `./runtime` (what
  `make clean-runtime` looks for), `homepage.*` labels, domain from `${X_DOMAIN}` in the
  tracked `.env`.
- **`lablr` is the stack to copy from, not `dozzle`.** It has the same shape as this
  one: an Authentik-gated UI beside an endpoint (`/agent`) that headless hardware
  reaches unauthenticated because it cannot follow a login redirect.
- **`./runtime/data` is owned by root when Docker creates it**, and the image runs as
  uid 10001. It is `chown`ed on the server; a fresh host needs that again before the
  relay can write SQLite there.
- **Deploying only this stack** is `git reset --hard origin/main` then
  `make up stack=strux-relay`. Plain `make deploy` would `up` every stack, including
  whatever else happens to be mid-flight on `main`.
- **Approvals live in `/app/data`.** Mount it, or re-pair every device after an update.

## Authentik

Applications are declarative, and applying one costs no downtime. No UI clicking: the
proxy provider, application and group binding are entries in
`stacks/authentik/blueprints/20-forward-auth.yaml`, and

```bash
docker exec authentik-worker ak apply_blueprint /blueprints/custom/20-forward-auth.yaml
```

applies them live — the outpost gets the new provider pushed to it and nothing restarts.

The one trap is in that file already: the outpost entry's `providers:` list **replaces**
what the outpost has, so a new proxy app must be added there as well as declared.

## Pairing, as it behaves in production

- **The device generates its own token**, the relay pins it on approval. No
  server→device message and no network-triggered NVS write; the secret only travels
  inside TLS, as an `X-Strux-Token` header on the upgrade request.
- **The refusal happens before the upgrade is accepted**, which is what gives both ends
  a reason: the device logs `upgrade refused with HTTP 403`, the relay logs
  `not approved` or `token mismatch` into the `events` table the dashboard renders.
- **Approval needs no push.** A refused device is already retrying — every 30 s, which
  is its own backoff for a refusal rather than the 5 s it uses for an unreachable
  server — so the reconnect after an approve is the one that succeeds. That *is* the
  handshake. It also means an unapproved device asks about twice a minute until somebody
  deals with it. Noisy on purpose.
- **An authenticated reconnect replaces the live pipe.** It proved itself, and refusing
  it would lock a rebooted device out until the dead socket timed out.
- **Two different tokens for one pending id means somebody is guessing**, so the
  dashboard shows both rather than collapsing them.
- **`forget` drops the live pipe too.** The token is only checked when a connection is
  made, so revoking without closing the socket would leave a forgotten device connected
  until it happened to reconnect.
- **The endpoint is rate limited per client address**, on failures only: twenty
  wrong connects in a minute and further attempts from that address are answered
  `429` with a `Retry-After` until the minute is up. A limited client is still
  *authenticated* — read-only, writing nothing down — so a device holding the right
  token is never shut out by a neighbour on the same NAT that is guessing, and its
  success clears the count. A refused device retries twice a minute, so twenty
  leaves room for ten unapproved boards behind one address. The count is per
  process and in memory; a restart forgives everybody.
- **A pending device's name is attacker-controlled** — on the legacy connect URL, which
  is the only way an unapproved device says anything at all. It lands in the pending
  list, which renders on an admin page, so the dashboard escapes it. Escaping is not
  decoration here. A device on the channels wire says nothing until it is approved,
  because it is refused before there is a socket to say it on.

## Device side

- **Settings involved:** `relay.enabled`, `relay.url`, `relay.deviceId`, `relay.token`.
- **TLS costs the device's relay task under 4 K.** Measured on an ESP32 right after the
  handshake: 6412 of 10240 bytes still free, so the 10 K stack is not close to tight. It
  is not a one-off number either — `CheckStackHeadroom()` logs every new low and WARNs
  under a quarter left.
- **The relay is payload-opaque, not frame-opaque.** It reads and rewrites the 3-byte
  channel header because it owns the id space on the device pipe, and it forwards or
  consumes the streams the device opens at it (`log stream` to every attached browser,
  `telemetry stream` to the sink). So it is not request/response, and it is not a tunnel
  either. On the retired wire those two streams were reserved ids (0 and 0xFFFF) instead
  of named channels; the relay still serves both wires and picks by the device's first
  frame.

## GHCR: a push denial is two faults wearing one error

Both say `denied`, and neither is explained by the workflow's token log, which reads
`Packages: write` in every case. Tell them apart by how many runs fail:

- **A secondary rate limit** — three pushes in quick succession got `denied: denied` on
  `docker login` once and `permission_denied … exceeded a secondary rate limit` on push
  once. Transient. Wait a few minutes and `gh run rerun <id> --failed`; nothing needs
  changing.
- **A package linked to another repository** — *every* run fails, forever, with
  `denied: permission_denied: write_package`. GHCR binds a package to the repo that
  first pushed it, and `ghcr.io/vanbassum/strux-relay` was first pushed by CI in
  **Strux** (tag `sha-434b0a9`), so the extracted repo's `GITHUB_TOKEN` had no write
  grant on it. Twelve consecutive runs failed across three hours before this was
  spotted, precisely because the note here said to wait it out.

  The fix is manual and there is no REST endpoint for it: the package's settings page →
  *Manage Actions access* → add the repository with **Write**. After the first
  successful push, `docker/metadata-action` stamps `org.opencontainers.image.source` at
  the new repo and GHCR re-links it, so the old grant can go.

  Do **not** delete the package to force a relink — `latest` is what production pulls.
