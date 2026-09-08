/**
 * What the main area is showing. Note what is NOT in here: which device is
 * selected.
 *
 * Selection outlives the view on purpose. Going back to the list is not
 * deselecting — the device stays in the sidebar so its pages are one click away,
 * and pressing another row is what changes it. Folding the id into the view
 * would have made "show the list" and "forget which device I was on" the same
 * action, which is why they were the same action before.
 */
export type View = { kind: "devices" } | { kind: "device"; page: string }

export const RELAY_HOME: View = { kind: "devices" }
