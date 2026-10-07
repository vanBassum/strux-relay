# Focus: reuse existing capabilities, don't build a parallel road

You check one thing: **does this PR build on what the relay already has, or does it
take the straightest road to the feature and build something beside it?**

## Investigate before you judge

1. **Read the problem in the PR description** (`gh pr view`). Separate the problem
   from the suggested implementation, and judge the PR against the problem.
2. **Name what the feature touches:** device pairing, the device pipe, browser
   relaying, the asset cache, the dashboard hub, the MCP surface, telemetry
   forwarding, persistence.
3. **Find the place that already owns it** using `README.md` and a few targeted
   greps. Keep this proportional; don't crawl the codebase.
4. **Only then compare.** Does the PR use that mechanism, extend it, or work around it?

## Look for

- **A parallel road:** a second way to reach a device beside the pipe and session
  routing; a second device store beside the registry and directory; a REST endpoint
  for the dashboard beside the SignalR hub; a second token or auth mechanism beside
  the existing ones; an MCP tool that duplicates what the generic tools already do.
- **Product knowledge in the relay:** code that knows a specific device's commands or
  settings. The relay serves any Strux device; what a device can do comes from the
  device (`help describe`, `system describe`), not from the relay.
- **Trust in the wrong place:** a decision the server should make left to the
  dashboard or to the device.
- **Conflicting concepts:** new terminology or state that gives an existing concept a
  second meaning.

Judge responsibilities and behaviour, not merely code shape. Prefer extending existing
mechanisms; don't demand a framework for imagined future needs.

## Reporting

Use `[risk]` for a parallel road, product knowledge or a conflicting concept that will
cost later work, `[bug]` only when behaviour is actually wrong, and `[convention]` for
smaller things. A `[risk]` or `[bug]` stops the automatic merge.
