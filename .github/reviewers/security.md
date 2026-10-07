# Focus: security, device isolation and exposure

You check whether the PR lets someone see, reach or change something they shouldn't.
The relay is on the public internet (behind Traefik and Authentik), and every device
behind it is someone's hardware.

Background: a device must be approved and present its own token (`X-Strux-Token`) or
its upgrade is refused with a 403; a pending device is refused before the upgrade and
nothing it sends is recorded. Browsers are authenticated by the reverse proxy (Authentik), and the dashboard's
pairing API deliberately has no check of its own: whoever can load the dashboard may
approve. Don't report that as missing authorization; do report a new route that the
proxy rules in `docs/operations.md` would leave unprotected (e.g. a `PathPrefix` that
catches a browser path on the device route). Agents
reach `/mcp` with a bearer token, or through the OAuth 2.1 flow under `/oauth/*`.

Look for:

- **Device impersonation.** A path where a device connects, or its hello is stored,
  without approval and a valid token; a token compared in a way that leaks timing or
  accepts an empty value; one device able to take over another's id or pipe.
- **Cross-device access.** A browser or agent session reaching a device it was not
  addressed to, or one device's replies, logs or cached assets served for another.
- **Missing or wrong authorization.** New endpoints, hub methods or MCP tools without
  the same protection as their neighbours; anything reachable anonymously that changes
  state (approve, revoke, issue a token).
- **Tokens and OAuth.** Tokens stored or logged in plaintext where a hash would do;
  tokens that never expire or can't be revoked; redirect URIs, `state` or PKCE not
  validated; headers trusted from the client when they should come from the proxy.
- **Input.** Untrusted device or browser data used in file paths, SQL, log lines or
  HTML without escaping; unbounded payloads that exhaust memory.
- **Secrets.** Connection strings, keys or tokens committed in `appsettings*.json`, the
  Dockerfile or code.
- **The review gate.** `.github/workflows/claude-review.yml` runs as the PR has it, so
  it gates itself; that is accepted, because a PR that touches `.github/workflows/` or
  `.github/reviewers/` is never merged automatically and Bas merges it by hand. Don't
  report the self-gating as such. Do report a change that weakens the gate or that
  trust boundary: a reviewer dropped from the matrix or from `REVIEWERS`, a verdict or
  path check loosened, or a rule that makes a blocking finding less likely.
