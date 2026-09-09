# Vendored: the shell contract

`contract.ts` is **not written here.** Its source of truth is
[`frontend/shell-contract/contract.ts` in vanBassum/Strux](https://github.com/vanBassum/Strux/blob/main/frontend/shell-contract/contract.ts),
and this is a byte-identical copy of it at the commit named in `contract.lock.json`.

Byte-identical, deliberately — not "the same with a vendoring banner on top". A banner
would mean the check could only compare *some* of the file, and the day the two copies
disagree in the part that was excluded is the day the check was worthless. So the
provenance lives in `contract.lock.json` and this README instead, and the copy itself is
the upstream file and nothing else.

## Why a copy at all

Firmware, the device shell and this shell all compile against the same `ShellProvider`,
so it is a cross-repo type dependency. A published package would need a registry, a
publish step and build-time auth for a file with **no imports and no runtime code beyond
one integer** — that does not pay for itself. The cost of copying is drift, and drift is
what the check below is for.

There is a second, independent detector at runtime: a module's device answers `ui modules`
with the `hostApi` range its firmware was built against, and this shell compares it with
the `HOST_API` in this file. Disjoint ranges mean "needs a newer shell", which is a
message rather than a crash. So a missed bump degrades visibly instead of silently.

## The check

`pnpm check:contract`, which `pnpm build` runs — so it fails the Docker image build, which
is this repo's CI.

| It fails when | Because |
|---|---|
| `contract.ts` no longer hashes to `sha256` in the lock | Somebody edited the vendored copy. Edits belong upstream |
| `contract.ts` differs from upstream **at the pinned commit** | The lock and the file disagree about what was vendored |

| It warns when | Because |
|---|---|
| Upstream's default branch has a different contract | Ahead is legitimate — this shell may deliberately speak an older `hostApi` and say so to a newer device. Worth knowing, not worth failing |
| GitHub is unreachable | The hash check still ran. A network outage is not a contract violation |

## Updating it

```bash
cd frontend
node scripts/check-contract.mjs --update    # copies upstream's default branch + rewrites the lock
pnpm typecheck                              # the shell must still implement the new shape
```

Then read the diff. A changed `HOST_API` is the part to think about: it means this shell's
own contract version moved, and every device built against the older one now negotiates
against it.
