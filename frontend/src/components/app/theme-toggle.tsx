import { MoonIcon, SunIcon } from "lucide-react"

import { Switch } from "@/components/ui/switch"
import { useTheme } from "@/components/theme-provider"

/**
 * Light/dark, as a switch between a sun and a moon.
 *
 * Two states for a setting that has three. "System" is the default and stays in
 * effect until somebody touches this, at which point they have expressed a
 * preference and a switch showing which way it went is exactly right — so the
 * control reads the RESOLVED theme and writes an explicit one. What it cannot do
 * is put "system" back; a third state would need a different control, and it is
 * not worth one until somebody asks to return to following the OS.
 */
export function ThemeToggle() {
  const { resolvedTheme, setTheme } = useTheme()
  const dark = resolvedTheme === "dark"

  return (
    <div className="flex items-center gap-1.5">
      <SunIcon
        className={`size-3.5 ${dark ? "text-muted-foreground/50" : "text-foreground"}`}
      />
      <Switch
        checked={dark}
        onCheckedChange={(next) => setTheme(next ? "dark" : "light")}
        // The provider also toggles on the "d" key, so the shortcut is worth
        // saying somewhere rather than being folklore.
        aria-label="Dark mode"
        title="Toggle dark mode (d)"
      />
      <MoonIcon
        className={`size-3.5 ${dark ? "text-foreground" : "text-muted-foreground/50"}`}
      />
    </div>
  )
}
