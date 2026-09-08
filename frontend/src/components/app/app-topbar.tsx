import { Button } from "@/components/ui/button"
import type { Page } from "@/components/app/navigation"
import type { ConnectionState } from "@/hooks/use-relay"

const DOTS: Record<ConnectionState, string> = {
  connected: "bg-emerald-500",
  connecting: "bg-amber-500",
  reconnecting: "bg-amber-500",
  disconnected: "bg-destructive",
}

export function AppTopbar({
  page,
  state,
  onRetry,
}: {
  page: Page
  state: ConnectionState
  onRetry: () => void
}) {
  return (
    <header className="flex h-12 shrink-0 items-center gap-2 border-b px-3">
      <span className="text-sm font-medium">{page}</span>

      <div className="ml-auto flex items-center gap-2">
        <span className="text-muted-foreground flex items-center gap-1.5 text-xs">
          <span className={`size-1.5 rounded-full ${DOTS[state]}`} />
          {state}
        </span>
        {/* Only when there is nothing to reconnect on its own: SignalR retries
            by itself, so a button during "reconnecting" would race it. */}
        {state === "disconnected" && (
          <Button size="sm" variant="outline" onClick={onRetry}>
            Retry
          </Button>
        )}
      </div>
    </header>
  )
}
