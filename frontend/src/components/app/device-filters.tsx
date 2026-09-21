import { cn } from "cn"

import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"

/**
 * A labelled dropdown over a fixed set of values. Its own component because the
 * markup is five nested elements and the page still has the page-size one.
 */
export function ChoiceFilter<T extends string>({
  value,
  options,
  onChange,
  label,
}: {
  value: T
  options: { value: T; label: string }[]
  onChange: (value: T) => void
  label: string
}) {
  return (
    <Select value={value} onValueChange={(next) => onChange(next as T)}>
      <SelectTrigger size="sm" aria-label={label}>
        {/* SelectValue renders the VALUE unless told otherwise, so without this
            the trigger read "all" — the stored value rather than the option's
            label, which said nothing about what the dropdown filters. */}
        <SelectValue>
          {(selected: T) =>
            options.find((option) => option.value === selected)?.label ?? selected
          }
        </SelectValue>
      </SelectTrigger>
      <SelectContent>
        {options.map((option) => (
          <SelectItem key={option.value} value={option.value}>
            {option.label}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  )
}

/**
 * Two mutually exclusive scopes as one control, with how many rows each would show.
 *
 * A segmented control rather than a third dropdown because there are exactly two
 * options and both are worth SEEING: the counts are the point — "Ready 3 | All 7"
 * says at a glance that four boards want looking at, which no closed dropdown can.
 *
 * Hand-rolled over two buttons rather than pulled in as another primitive: it is a
 * radio group with two options, drawn from tokens the app already owns. h-8 to
 * match the search input beside it.
 */
export function ScopeFilter<T extends string>({
  value,
  options,
  onChange,
  label,
}: {
  value: T
  options: { value: T; label: string; count: number }[]
  onChange: (value: T) => void
  label: string
}) {
  return (
    // role=radiogroup, not a row of buttons: the two are exclusive and one is
    // always chosen, which is what a screen reader needs told.
    <div
      role="radiogroup"
      aria-label={label}
      className="text-muted-foreground inline-flex h-8 shrink-0 items-center gap-0.5 rounded-lg bg-muted p-0.5"
    >
      {options.map((option) => {
        const selected = option.value === value
        return (
          <button
            key={option.value}
            type="button"
            role="radio"
            aria-checked={selected}
            onClick={() => onChange(option.value)}
            className={cn(
              "inline-flex h-7 items-center gap-1.5 rounded-[min(var(--radius-md),12px)] px-2.5 text-[0.8rem] font-medium whitespace-nowrap transition-colors outline-none focus-visible:ring-3 focus-visible:ring-ring/50",
              selected
                ? "bg-background text-foreground shadow-sm"
                : "hover:text-foreground"
            )}
          >
            {option.label}
            {/* Tabular so the control does not twitch as counts change under a
                live list, and dimmed because the count qualifies the word rather
                than competing with it. */}
            <span className="text-xs tabular-nums opacity-60">
              {option.count}
            </span>
          </button>
        )
      })}
    </div>
  )
}
