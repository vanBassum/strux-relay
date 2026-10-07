# Focus: structure, contracts and conventions

You check whether the PR follows the structure and conventions described in
`README.md` and visible in the surrounding code.

Look for:

- **Provider-neutral data model.** No raw SQL, no provider-specific defaults or column
  types, UTC `DateTime` rather than `DateTimeOffset`. A model change comes with a
  migration in `api/Migrations`.
- **Where things live.** Device transport in `api/Devices`, the dashboard API in
  `api/Hubs`, the agent surface in `api/Mcp`, telemetry forwarding in `api/Telemetry`,
  persistence in `api/Data`. New code follows the existing split rather than growing
  `Program.cs`.
- **The device contract.** The relay stores what a device's hello says and ignores
  keys it does not understand; it never pushes anything to a device. Identity comes
  only from the connect URL and token. A change that requires a new device-side field
  without tolerating its absence breaks older firmware.
- **The dashboard.** `frontend/` is React + shadcn/ui; it talks to the server through
  `/hub`, not through new ad-hoc HTTP endpoints. `pnpm build` writes `api/wwwroot`;
  built output is not hand-edited.
- **Generic MCP surface.** The MCP tools are generic over any device; a tool that
  knows one product's command names belongs to that product, not the relay.
- **Tests.** New logic that is easy to unit-test (chunk parsing, routing, token and
  OAuth checks) comes with a test in `tests/StruxRelay.Tests`.
- **Docs.** A change to an endpoint, a setting or the handshake updates `README.md`,
  and a change to how the relay is deployed or a settled decision updates
  `docs/operations.md`, in the same PR.
