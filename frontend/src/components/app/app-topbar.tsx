import { ChevronRightIcon } from "lucide-react"

import { Button } from "@/components/ui/button"
import { Separator } from "@/components/ui/separator"
import { ThemeToggle } from "@/components/app/theme-toggle"
import type { ConnectionState } from "@/hooks/use-relay"

const DOTS: Record<ConnectionState, string> = {
  connected: "bg-emerald-500",
  connecting: "bg-amber-500",
  reconnecting: "bg-amber-500",
  disconnected: "bg-destructive",
}

export type Crumb = { label: string; onClick?: () => void }

export function AppTopbar({
  trail,
  state,
  onRetry,
}: {
  /** Last entry is where you are; earlier ones with an onClick are the way back. */
  trail: Crumb[]
  state: ConnectionState
  onRetry: () => void
}) {
  return (
    <header className="flex h-12 shrink-0 items-center gap-2 border-b px-3">
      <nav className="flex min-w-0 items-center gap-1 text-sm">
        {trail.map((crumb, index) => {
          const last = index === trail.length - 1
          return (
            <span key={crumb.label} className="flex min-w-0 items-center gap-1">
              {index > 0 && (
                <ChevronRightIcon className="text-muted-foreground size-3.5 shrink-0" />
              )}
              {crumb.onClick && !last ? (
                <button
                  type="button"
                  className="text-muted-foreground hover:text-foreground truncate"
                  onClick={crumb.onClick}
                >
                  {crumb.label}
                </button>
              ) : (
                <span
                  className={`truncate ${last ? "font-medium" : "text-muted-foreground"}`}
                >
                  {crumb.label}
                </span>
              )}
            </span>
          )
        })}
      </nav>

      <div className="ml-auto flex shrink-0 items-center gap-2">
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
        <Separator orientation="vertical" className="!h-4" />
        <ThemeToggle />
      </div>
    </header>
  )
}
