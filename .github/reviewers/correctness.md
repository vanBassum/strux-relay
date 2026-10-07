# Focus: logic, concurrency and data correctness

You check whether the PR's logic is right in a long-running server holding many
WebSocket pipes at once.

Look for:

- **Logic bugs.** Off-by-one errors, wrong conditions, null dereferences on optional
  data, unhandled edge cases (a device that reconnects while the old pipe is still
  open, a browser that disconnects mid-reply, an empty or oversized chunk, a device
  that never sends a hello).
- **Session routing.** Chunks delivered to the wrong browser or session; session ids
  reused while a previous session is still open; `FLAG_FINAL`/`FLAG_REJECT` not ending
  a session, so it leaks; reserved sessions (0, `0xFFFE`, `0xFFFF`) routed as replies.
- **Concurrency.** Shared state (registries, dictionaries, pipes) touched from several
  connections without synchronisation; two writers on one `WebSocket` at the same time
  (not allowed); `async void`, un-awaited tasks, missing `CancellationToken` so work
  outlives its connection; `.Result`/`.Wait()` that can deadlock.
- **Resources.** Sockets, streams or `DbContext`s not disposed; a `DbContext` used
  outside its scope or from several threads; unbounded buffers or queues per device.
- **Data.** Changes that never reach `SaveChangesAsync`, migrations that drop or
  rename columns and lose data, queries that pull a whole table into memory.
- **Caching.** The asset cache serving one device's or one firmware version's files
  for another.
- **Frontend.** For changes under `frontend/`: stale `useEffect` dependencies, effects
  without cleanup, SignalR handlers registered twice or never removed.
