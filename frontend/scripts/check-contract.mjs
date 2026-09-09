// Guards the one file this repo does not own: shell-contract/contract.ts.
//
// It is a byte-identical copy of Strux's own, and both this shell and every UI module
// compile against it. The failure it exists for is silent: a shape that has moved
// upstream still type-checks here, so the relay shell keeps implementing last month's
// contract and the mismatch only turns up as a page that renders nothing.
//
// Three checks, and what separates them is whether the answer is knowable offline:
//
//   1. The vendored file hashes to what the lock says.  Always. Catches a local edit,
//      which is how drift actually starts — somebody needs one more field and adds it
//      in the copy.
//   2. It matches upstream AT THE PINNED COMMIT.        Network. Catches a lock that
//      does not describe the file next to it.
//   3. Upstream's default branch, for information.      Network. AHEAD IS LEGAL: this
//      shell may deliberately speak an older hostApi, and the manifest check tells a
//      device so at runtime. A warning, never a failure.
//
// `--update` re-vendors from the default branch and rewrites the lock.
//
//   node scripts/check-contract.mjs [--update]

import { readFile, writeFile } from "node:fs/promises"
import { createHash } from "node:crypto"
import { join } from "node:path"

const here = import.meta.dirname
const contractPath = join(here, "../shell-contract/contract.ts")
const lockPath = join(here, "../shell-contract/contract.lock.json")

const update = process.argv.includes("--update")

/// Line endings are normalised before hashing. Git may check the same bytes out as CRLF
/// in one repo and LF in another depending on autocrlf, and a check that called that a
/// contract violation would fail on a fresh clone for no reason.
function digest(text) {
  return createHash("sha256").update(text.replace(/\r\n/g, "\n")).digest("hex")
}

function raw(repo, ref, path) {
  return `https://raw.githubusercontent.com/${repo}/${ref}/${path}`
}

/// Returns the text, or null when GitHub could not be reached. A 404 is NOT null — it
/// is an answer, and a bad one: it means the pinned commit or path is gone.
async function fetchUpstream(url) {
  const response = await fetch(url, { redirect: "follow" })
  if (!response.ok) throw new Error(`${response.status} ${response.statusText} for ${url}`)
  return response.text()
}

const lock = JSON.parse(await readFile(lockPath, "utf8"))
const { repo, path: upstreamPath } = lock

// ── --update: re-vendor and rewrite the lock ────────────────────────────────────

if (update) {
  const meta = await (await fetch(`https://api.github.com/repos/${repo}`)).json()
  const branch = meta.default_branch ?? "main"

  const commits = await (
    await fetch(
      `https://api.github.com/repos/${repo}/commits?sha=${branch}&path=${encodeURIComponent(upstreamPath)}&per_page=1`,
    )
  ).json()
  const commit = commits?.[0]?.sha
  if (!commit) throw new Error(`could not resolve the latest commit touching ${upstreamPath}`)

  const text = await fetchUpstream(raw(repo, commit, upstreamPath))
  const hostApi = Number(/HOST_API\s*:\s*HostApiVersion\s*=\s*(\d+)/.exec(text)?.[1] ?? NaN)
  if (!Number.isInteger(hostApi))
    throw new Error("vendored contract does not declare HOST_API — refusing to write a lock that lies")

  await writeFile(contractPath, text)
  await writeFile(
    lockPath,
    JSON.stringify(
      {
        $comment: lock.$comment,
        repo,
        path: upstreamPath,
        commit,
        sha256: digest(text),
        hostApi,
        vendoredAt: new Date().toISOString().slice(0, 10),
      },
      null,
      2,
    ) + "\n",
  )

  console.log(`check-contract: re-vendored from ${repo}@${commit.slice(0, 10)} (hostApi ${hostApi})`)
  console.log("  now run `pnpm typecheck` — the shell has to implement whatever changed.")
  process.exit(0)
}

// ── 1. the vendored file is what the lock describes ─────────────────────────────

const vendored = await readFile(contractPath, "utf8")
const actual = digest(vendored)

if (actual !== lock.sha256) {
  console.error("\ncheck-contract: the vendored contract has been edited\n")
  console.error(`  shell-contract/contract.ts hashes to ${actual}`)
  console.error(`  the lock says                        ${lock.sha256}`)
  console.error(
    `\nThis file is a copy of ${upstreamPath} in ${repo}. Change it there, then re-vendor:\n` +
      "  node scripts/check-contract.mjs --update\n",
  )
  process.exit(1)
}

// The lock's hostApi is a second, human-readable record of the same fact, so it must
// not be able to disagree with the file.
const declared = Number(/HOST_API\s*:\s*HostApiVersion\s*=\s*(\d+)/.exec(vendored)?.[1] ?? NaN)
if (declared !== lock.hostApi) {
  console.error(
    `\ncheck-contract: contract.ts declares HOST_API ${declared}, the lock says ${lock.hostApi}\n`,
  )
  process.exit(1)
}

// ── 2 & 3. against upstream ─────────────────────────────────────────────────────

let pinned
try {
  pinned = await fetchUpstream(raw(repo, lock.commit, upstreamPath))
} catch (error) {
  // Only reached for a network failure or a 4xx. Either way check 1 has passed, so the
  // file is the one that was reviewed; this half is about provenance, not content.
  console.warn(`check-contract: could not reach upstream (${error.message})`)
  console.warn(
    `  hash check passed, so the vendored contract is unmodified — provenance unverified.`,
  )
  console.log(`check-contract: contract.ts OK (hostApi ${declared}, upstream not checked)`)
  process.exit(0)
}

if (digest(pinned) !== actual) {
  console.error("\ncheck-contract: the lock does not describe the vendored file\n")
  console.error(
    `  ${repo}@${lock.commit.slice(0, 10)}:${upstreamPath} differs from the copy in this repo.\n` +
      "  The hash matched, so the lock's sha256 and its commit disagree with each other —\n" +
      "  somebody hand-edited the lock. Re-vendor:\n" +
      "    node scripts/check-contract.mjs --update\n",
  )
  process.exit(1)
}

let ahead = null
try {
  const meta = await (await fetch(`https://api.github.com/repos/${repo}`)).json()
  const head = await fetchUpstream(raw(repo, meta.default_branch ?? "main", upstreamPath))
  if (digest(head) !== actual) {
    ahead = Number(/HOST_API\s*:\s*HostApiVersion\s*=\s*(\d+)/.exec(head)?.[1] ?? NaN)
  }
} catch {
  // The default branch may not carry the contract yet — it lands there when the module
  // work merges. Not a failure: checks 1 and 2 already passed.
}

if (ahead !== null) {
  console.warn(
    `check-contract: ${repo}'s default branch has a NEWER contract` +
      (Number.isInteger(ahead) ? ` (hostApi ${ahead} vs ${declared} here)` : ""),
  )
  console.warn(
    "  Not a failure — this shell may deliberately speak the older one, and it tells a\n" +
      "  device so through the manifest's hostApi range. Re-vendor when you want it:\n" +
      "    node scripts/check-contract.mjs --update",
  )
}

console.log(
  `check-contract: contract.ts OK — byte-identical to ${repo}@${lock.commit.slice(0, 10)}, hostApi ${declared}`,
)
