/**
 * What the shell is showing.
 *
 * Two kinds rather than a flat page name, because a device page is only
 * meaningful together with the device it belongs to — a `page` on its own could
 * not say which board's Configuration it meant. Keeping the device id in the
 * view is also what lets the sidebar scope itself without a second source of
 * truth about "the current device".
 */
export type View =
  | { kind: "devices" }
  | { kind: "device"; deviceId: string; page: string }

export const RELAY_HOME: View = { kind: "devices" }
