import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"

/**
 * A labelled dropdown over a fixed set of values. Its own component because the
 * page has three of them and the markup is five nested elements each.
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
