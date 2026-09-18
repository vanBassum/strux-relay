import { useCallback, useEffect, useState } from "react"
import { toast } from "sonner"

import { useRelayContext } from "@/hooks/use-relay"

/** One MCP credential. Never the token itself — the relay keeps only a hash. */
export type McpToken = {
  id: string
  name: string
  /** The token's first few characters, for matching a row against a config file. */
  hint: string
  createdAt: string
  lastUsedAt: string | null
  revokedAt: string | null
}

export type McpState = {
  /**
   * Whether the endpoint will answer anything at all. False when no credential
   * exists in either place, which is a real state rather than a misconfiguration:
   * a relay nobody has issued a token for refuses every MCP request.
   */
  enabled: boolean
  /** Whether the deployment passed one in. That one is rotated in the secrets file. */
  deploymentTokenConfigured: boolean
  tokens: McpToken[]
}

/**
 * The MCP page's state, and the three things it can do.
 *
 * A created token is returned ONCE, by the call that creates it, and is held in
 * component state from there — it is never in this list and cannot be fetched
 * again, because the relay stored a hash of it and nothing else.
 */
export function useMcp() {
  const { invoke, on, onConnected } = useRelayContext()
  const [state, setState] = useState<McpState | null>(null)
  const [loading, setLoading] = useState(true)

  const reload = useCallback(async () => {
    try {
      setState(await invoke<McpState>("GetMcp"))
    } catch {
      /* the connection indicator is already saying it */
    } finally {
      setLoading(false)
    }
  }, [invoke])

  useEffect(() => onConnected(() => void reload()), [onConnected, reload])
  useEffect(() => on("McpTokensChanged", () => void reload()), [on, reload])

  /** Returns the plaintext token, once, or null when it could not be created. */
  const create = useCallback(
    async (name: string): Promise<string | null> => {
      try {
        const result = await invoke<{
          ok: boolean
          token: string | null
          error: string | null
        }>("CreateMcpToken", name)
        if (!result.ok || !result.token) {
          toast.error(result.error ?? "Could not create that token.")
          return null
        }
        return result.token
      } catch {
        toast.error("Could not reach the relay.")
        return null
      }
    },
    [invoke]
  )

  const revoke = useCallback(
    async (token: McpToken) => {
      try {
        const result = await invoke<{ ok: boolean; error: string | null }>(
          "RevokeMcpToken",
          token.id
        )
        if (!result.ok) {
          toast.error(result.error ?? "Could not revoke that token.")
          return
        }
        // Immediately, not on the next reconnect: every MCP request is checked, so
        // there is no session left holding the old answer.
        toast.success(`"${token.name}" no longer works.`)
      } catch {
        toast.error("Could not reach the relay.")
      }
    },
    [invoke]
  )

  const forget = useCallback(
    async (token: McpToken) => {
      try {
        const result = await invoke<{ ok: boolean; error: string | null }>(
          "ForgetMcpToken",
          token.id
        )
        if (!result.ok) toast.error(result.error ?? "Could not remove that row.")
      } catch {
        toast.error("Could not reach the relay.")
      }
    },
    [invoke]
  )

  return { state, loading, create, revoke, forget }
}

export type McpList = ReturnType<typeof useMcp>
