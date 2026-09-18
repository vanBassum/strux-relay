import { useState } from "react"
import {
  CheckIcon,
  CopyIcon,
  KeyRoundIcon,
  PlusIcon,
  SparklesIcon,
  Trash2Icon,
  TriangleAlertIcon,
} from "lucide-react"
import { toast } from "sonner"

import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import { useMcp, type McpToken } from "@/hooks/use-mcp"
import { useNow } from "@/hooks/use-now"
import { absolute, ago, date } from "@/lib/time"

/**
 * Where an MCP client points. Derived from where this page was served, like the
 * device URL is, so it is right behind a reverse proxy without the relay being told
 * its own public name.
 */
function mcpUrl(): string {
  return `${window.location.origin}/mcp`
}

/** The placeholder a snippet carries when there is no fresh token to put in it. */
const PLACEHOLDER = "<paste your token here>"

function clientConfig(url: string, token: string): string {
  return JSON.stringify(
    {
      mcpServers: {
        strux: {
          type: "http",
          url,
          headers: { Authorization: `Bearer ${token}` },
        },
      },
    },
    null,
    2
  )
}

/**
 * The block somebody pastes into a chat to hand an assistant this relay.
 *
 * Deliberately prose and not a schema: the tools describe themselves once a client
 * is connected, so what this has to carry is the part a tool list does not — where
 * to connect, what the thing on the other end IS, and the two facts that stop an
 * agent guessing wrong (a device has to be exposed here first, and `execute` drives
 * real hardware).
 */
function aiBriefing(url: string, token: string): string {
  return `I have a Strux relay with an MCP endpoint. Please connect to it and use it.

  URL:  ${url}
  Auth: send the HTTP header  Authorization: Bearer ${token}
        (ChatGPT instead: add it as a connector with this URL and pick OAuth —
         it registers itself, you approve it once in the browser, and no token
         from this page is needed)

MCP client config (Claude Desktop / Claude Code style):

${clientConfig(url, token)}

Or, with the Claude Code CLI:

  claude mcp add --transport http strux ${url} --header "Authorization: Bearer ${token}"

What is on the other end: a relay that ESP32 devices running Strux firmware dial out
to. It exposes exactly three tools, and they are generic — the relay knows nothing
about what any device does, so everything you learn comes from the device itself:

  devices()                              list the devices reachable right now, with a
                                         one-line description of what each one is
  describe(deviceId)                     that device's own instructions plus every
                                         command it has, with each argument's name,
                                         type, whether it is required, and what it
                                         means
  execute(deviceId, command, arguments)  run one of those commands and get the reply

Start with devices, then describe the one you want, then execute. Read the device's
instructions before composing calls: they explain how that particular product is meant
to be driven. The command is the two-word route describe reports, such as
"system info" or "led set", and the arguments are a JSON object of the names that
command declares.

Two things worth knowing before you act:

  * a device only appears if a human has switched MCP exposure on for it here, so an
    empty list means there is nothing to drive rather than something being broken;
  * execute talks to real hardware immediately and without a confirmation step. Some
    commands write flash or reboot a board, and each command's description says so —
    read it before you call it.`
}

/** A copy button that says it copied and then stops saying it. */
function CopyButton({
  text,
  label,
  size = "sm",
}: {
  text: string
  label: string
  size?: "sm" | "icon-sm"
}) {
  const [copied, setCopied] = useState(false)

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(text)
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    } catch {
      // Clipboard access needs a secure context, so plain http on a LAN address
      // refuses it. Saying so beats a button that silently does nothing.
      toast.error("Could not copy — select the text and copy it by hand.")
    }
  }

  return (
    <Button
      size={size === "icon-sm" ? "icon-sm" : "sm"}
      variant="outline"
      aria-label={label}
      onClick={() => void copy()}
    >
      {copied ? <CheckIcon /> : <CopyIcon />}
      {size === "sm" && (copied ? "Copied" : label)}
    </Button>
  )
}

/** A scrollable, monospaced block with its own copy button. */
function Snippet({ text, label }: { text: string; label: string }) {
  return (
    <div className="relative">
      <pre className="bg-muted max-h-72 overflow-auto rounded-md p-3 pr-24 font-mono text-xs leading-relaxed whitespace-pre-wrap">
        {text}
      </pre>
      <div className="absolute top-2 right-2">
        <CopyButton text={text} label={label} />
      </div>
    </div>
  )
}

export function McpPage() {
  const { state, loading, create, revoke, forget } = useMcp()
  const [name, setName] = useState("")
  const [creating, setCreating] = useState(false)
  // The one copy of a new token that will ever exist, held until the page is left.
  // Not in the list, and not fetchable: the relay stored a hash of it.
  const [fresh, setFresh] = useState<{ name: string; token: string } | null>(null)

  useNow()

  const url = mcpUrl()
  const token = fresh?.token ?? PLACEHOLDER
  const tokens = state?.tokens ?? []
  const live = tokens.filter((entry) => !entry.revokedAt)

  const submit = async () => {
    setCreating(true)
    const created = await create(name)
    setCreating(false)
    if (!created) return
    setFresh({ name: name.trim(), token: created })
    setName("")
    toast.success("Token created — copy it now, it is not shown again.")
  }

  return (
    <div className="flex flex-col gap-4 p-4">
      {/* ── What to give an assistant ─────────────────────────────────────── */}
      <Card>
        <CardHeader className="flex-row items-center justify-between gap-3 space-y-0">
          <CardTitle className="flex items-center gap-2">
            <SparklesIcon className="size-4" />
            Endpoint
          </CardTitle>
          <div className="flex items-center gap-2">
            {state && !state.enabled && (
              <Badge
                variant="outline"
                className="border-amber-500/30 bg-amber-500/15 text-amber-700 dark:text-amber-400"
                title="No credential exists, so every MCP request is refused"
              >
                Refusing everything
              </Badge>
            )}
            <code className="bg-muted rounded-md px-2 py-1.5 font-mono text-xs">
              {url}
            </code>
            <CopyButton text={url} label="Copy the MCP URL" size="icon-sm" />
          </div>
        </CardHeader>
        <CardContent className="flex flex-col gap-4">
          <div className="flex flex-col gap-2">
            <div className="flex items-center justify-between gap-2">
              <span className="text-sm font-medium">Paste this to an assistant</span>
              {!fresh && (
                <span className="text-muted-foreground text-xs">
                  Create a token below to have it filled in
                </span>
              )}
            </div>
            <Snippet text={aiBriefing(url, token)} label="Copy briefing" />
          </div>

          <div className="flex flex-col gap-2">
            <span className="text-sm font-medium">MCP client config</span>
            <Snippet text={clientConfig(url, token)} label="Copy config" />
          </div>

          {/* ChatGPT is the reason this relay runs an authorization server at all:
              its connector UI has no field for a token. Saying so here saves
              somebody pasting one into a box that does not exist. */}
          <p className="text-muted-foreground text-xs">
            <strong className="text-foreground">ChatGPT</strong> does not take a
            token: add a connector with the URL above, choose{" "}
            <strong className="text-foreground">OAuth</strong>, and approve it once
            in the browser. The grant then appears in the list below and can be
            revoked there like any other.
          </p>
        </CardContent>
      </Card>

      {/* ── The one time a token is readable ──────────────────────────────── */}
      {fresh && (
        <Card className="border-emerald-500/40">
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <KeyRoundIcon className="size-4" />
              “{fresh.name}” — copy it now
            </CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col gap-3">
            <p className="text-muted-foreground text-sm">
              This is the only time this token can be read. The relay keeps a hash of
              it and nothing else, so nothing here can show it again — if it is lost,
              revoke it and make another.
            </p>
            <div className="flex items-center gap-2">
              <code className="bg-muted flex-1 overflow-x-auto rounded-md px-3 py-2 font-mono text-xs">
                {fresh.token}
              </code>
              <CopyButton text={fresh.token} label="Copy the token" />
            </div>
            <div>
              <Button variant="ghost" size="sm" onClick={() => setFresh(null)}>
                I have it — hide this
              </Button>
            </div>
          </CardContent>
        </Card>
      )}

      {/* ── The credentials themselves ────────────────────────────────────── */}
      <Card>
        <CardHeader className="flex-row items-center justify-between gap-3 space-y-0">
          <CardTitle className="flex items-center gap-2">
            <KeyRoundIcon className="size-4" />
            Tokens
            <span className="text-muted-foreground text-sm font-normal">
              {live.length} active
            </span>
          </CardTitle>

          <Dialog>
            <DialogTrigger
              render={
                <Button size="sm">
                  <PlusIcon />
                  New token
                </Button>
              }
            />
            <DialogContent>
              <DialogHeader>
                <DialogTitle>New MCP token</DialogTitle>
                <DialogDescription>
                  Name it after whatever will hold it — a laptop, an assistant, a
                  script. The name is the only way to tell later which one to revoke.
                </DialogDescription>
              </DialogHeader>
              <Input
                autoFocus
                placeholder="Claude on my laptop"
                value={name}
                maxLength={60}
                onChange={(event) => setName(event.target.value)}
                onKeyDown={(event) => {
                  if (event.key === "Enter" && name.trim()) void submit()
                }}
              />
              <DialogFooter>
                <DialogClose render={<Button variant="ghost">Cancel</Button>} />
                <DialogClose
                  render={
                    <Button
                      disabled={!name.trim() || creating}
                      onClick={() => void submit()}
                    >
                      Create
                    </Button>
                  }
                />
              </DialogFooter>
            </DialogContent>
          </Dialog>
        </CardHeader>

        <CardContent>
          {state?.deploymentTokenConfigured && (
            // Shown, never editable. It comes from the deployment's secrets file and
            // a button here that disagreed with that file would be worse than no
            // button — but a credential nobody can see is worse than both.
            <div className="text-muted-foreground mb-3 flex items-start gap-2 rounded-md border border-dashed p-3 text-xs">
              <TriangleAlertIcon className="mt-0.5 size-3.5 shrink-0" />
              <span>
                A deployment token is also configured, in this relay's environment
                (<code className="font-mono">Relay__Mcp__Token</code>). It works like
                the tokens below and cannot be revoked from here — rotate it in the
                deployment's secrets file.
              </span>
            </div>
          )}

          <div className="rounded-lg border">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Name</TableHead>
                  <TableHead>Created</TableHead>
                  <TableHead>Last used</TableHead>
                  <TableHead className="text-right">Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {tokens.length === 0 ? (
                  <TableRow>
                    <TableCell colSpan={4} className="text-muted-foreground py-8">
                      <span className="flex items-center justify-center gap-2">
                        <KeyRoundIcon className="size-4" />
                        {loading
                          ? "Loading…"
                          : "No tokens yet — create one to let an assistant in."}
                      </span>
                    </TableCell>
                  </TableRow>
                ) : (
                  tokens.map((entry) => (
                    <TokenRow
                      key={entry.id}
                      token={entry}
                      onRevoke={revoke}
                      onForget={forget}
                    />
                  ))
                )}
              </TableBody>
            </Table>
          </div>
        </CardContent>
      </Card>
    </div>
  )
}

function TokenRow({
  token,
  onRevoke,
  onForget,
}: {
  token: McpToken
  onRevoke: (token: McpToken) => Promise<void>
  onForget: (token: McpToken) => Promise<void>
}) {
  const revoked = token.revokedAt !== null
  // An OAuth grant lapses on its own, and its client renews it silently — so an
  // expired row is usually a connector nobody has used since, not a problem.
  const expired =
    token.expiresAt !== null && new Date(token.expiresAt).getTime() < Date.now()

  return (
    <TableRow className={revoked ? "opacity-60" : undefined}>
      <TableCell>
        <div className="flex items-center gap-2 font-medium">
          {token.name}
          {/* Where it came from, only when that is not the ordinary case: a row with
              no badge is one somebody created on this page. */}
          {token.kind === "oauth" && (
            <Badge variant="outline" title="Granted through the OAuth consent screen">
              OAuth
            </Badge>
          )}
          {revoked ? (
            <Badge variant="outline" title={absolute(token.revokedAt)}>
              Revoked
            </Badge>
          ) : (
            expired && (
              <Badge variant="outline" title={absolute(token.expiresAt)}>
                Expired
              </Badge>
            )
          )}
        </div>
        {/* Enough of the token to match it against a config file, and nowhere near
            enough to be one. */}
        <div className="text-muted-foreground font-mono text-xs">
          {token.hint}…
        </div>
      </TableCell>

      <TableCell title={absolute(token.createdAt)}>{date(token.createdAt)}</TableCell>

      {/* Never used is a fact worth stating plainly: it is the answer to "did that
          client ever actually connect". */}
      <TableCell title={absolute(token.lastUsedAt)}>
        {token.lastUsedAt ? ago(token.lastUsedAt) : "never used"}
        {token.expiresAt && !revoked && (
          <div className="text-muted-foreground text-xs" title={absolute(token.expiresAt)}>
            {expired ? "expired" : `expires ${ago(token.expiresAt)}`}
          </div>
        )}
      </TableCell>

      <TableCell className="text-right">
        {revoked ? (
          <Button
            size="sm"
            variant="ghost"
            onClick={() => void onForget(token)}
            title="Remove this row for good"
          >
            <Trash2Icon />
            Remove
          </Button>
        ) : (
          <Button
            size="sm"
            variant="ghost"
            className="text-destructive"
            onClick={() => void onRevoke(token)}
            title="Stop this token working, starting with the next request"
          >
            <Trash2Icon />
            Revoke
          </Button>
        )}
      </TableCell>
    </TableRow>
  )
}
