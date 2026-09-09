// This shell's implementation of the contract's `DeviceTransport`.
//
// One hub invoke, and deliberately nothing more. The relay already owns a
// `DeviceConnection` per device — session ids, the single-in-flight gate, chunk
// reassembly, the idle watchdog — so a command from a module rides that, over the
// pipe the device dialled out on. What this file must NOT do is open a socket to the
// device: this shell is multi-device, so a browser-side pipe would mean one socket per
// device with its own reconnect, auth handshake and lifecycle, reimplementing in a
// browser what `DeviceConnection` already is. The device's own browser pipe at
// /devices/<id>/ws still exists and still serves the whole-page mode; it is not this.
//
// A module written against the contract therefore runs unchanged on both shells: on
// the device it is a WebSocket session, here it is `DeviceCommand`, and the module
// cannot tell.

import type { DeviceTransport } from "@shell/contract"

type Invoke = <T>(method: string, ...args: unknown[]) => Promise<T>

/// What the hub's DeviceCommand answers. Failure is DATA, not a thrown exception —
/// SignalR rewrites a thrown message ("An unexpected error occurred invoking … on the
/// server. HubException: unknown command"), and the contract promises a module the
/// device's own words. See DeviceCommandResult on the server for the whole reason.
type DeviceCommandResult = {
  ok: boolean
  reply: string | null
  error: string | null
  /// True when the DEVICE declined, false when the pipe failed. Not used yet; it is
  /// the one bit of the wire's flags a caller could not otherwise recover.
  refused: boolean
}

/// Cached per device, because a module holds on to the object it was handed and a new
/// one every render would be a new identity in every dependency list a module wrote.
/// Keyed on the invoke too: it changes when the hub connection is rebuilt, and a
/// transport closing over the old one would call into a dead connection.
const cache = new WeakMap<object, Map<string, DeviceTransport>>()

export function deviceTransport(invoke: Invoke, deviceId: string): DeviceTransport {
  let perDevice = cache.get(invoke)
  if (!perDevice) {
    perDevice = new Map()
    cache.set(invoke, perDevice)
  }

  const existing = perDevice.get(deviceId)
  if (existing) return existing

  const transport: DeviceTransport = {
    async request<T = unknown>(
      command: string,
      args?: Record<string, unknown>,
    ): Promise<T> {
      // The reply travels as text and is parsed here, because the relay has no
      // business understanding a command's shape — it owns the session header and
      // nothing below it. Anything that re-serialised on the way through would be a
      // second place that has to agree about every reply's fields.
      const result = await invoke<DeviceCommandResult>(
        "DeviceCommand",
        deviceId,
        command,
        args ?? {},
      )

      // Rejected here, from the reason the device gave, which is exactly what the
      // contract promises. A relay that is down rejects earlier and differently —
      // `invoke` itself throws "Not connected to the relay." — and a device that is
      // offline arrives as an ordinary failed result. All three are an Error with a
      // message a page can show, and the contract asks for no taxonomy beyond that.
      if (!result.ok) throw new Error(result.error || "the device did not answer")

      // A handler that wrote no reply at all. Every command writes through
      // ReplyWriter so this should not happen, but JSON.parse("") throws a
      // SyntaxError that would read as the device having said something malformed.
      if (!result.reply) return undefined as T

      return JSON.parse(result.reply) as T
    },
  }

  perDevice.set(deviceId, transport)
  return transport
}
