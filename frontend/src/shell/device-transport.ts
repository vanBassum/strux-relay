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

import type { DeviceLogLine, DeviceTransport } from "@shell/contract"

type Invoke = <T>(method: string, ...args: unknown[]) => Promise<T>
type On = <T>(event: string, handler: (payload: T) => void) => () => void

/// What this factory needs from the hub. A slice rather than the whole `Relay`, so it
/// is obvious that a transport can call and can listen, and can do nothing else.
export interface TransportHub {
  invoke: Invoke
  on: On
}

/// One "DeviceLog" push carries one line and says which device it came from — every
/// subscriber sees every device's, so filtering is the reader's job.
interface DeviceLogPush {
  deviceId: string
  line: string
}

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

/// `?command=led+get&partition=ota_1` — the command plus every argument, flat, which
/// is the shape a device envelope has anyway. Values go as strings and the device's own
/// ArgReader types them, because it is the only thing that knows a partition's or a
/// setting's rules.
function query(command: string, args?: Record<string, unknown>): string {
  const params = new URLSearchParams({ command })
  for (const [key, value] of Object.entries(args ?? {}))
    if (key !== "command" && value !== undefined && value !== null)
      params.set(key, String(value))
  return `?${params}`
}

/// The relay answers a device-side failure as ProblemDetails, so its `detail` is the
/// DEVICE's own reason — which is what the contract promises a module.
function problemDetail(request: XMLHttpRequest): string {
  try {
    return JSON.parse(request.responseText)?.detail ?? `HTTP ${request.status}`
  } catch {
    return `HTTP ${request.status}`
  }
}

export function deviceTransport(hub: TransportHub, deviceId: string): DeviceTransport {
  const { invoke, on } = hub

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

    async upload<T = unknown>(
      command: string,
      args: Record<string, unknown> | undefined,
      body: Blob,
      onProgress?: (fraction: number) => void,
    ): Promise<T> {
      // HTTP, not the hub, because this is a BODY: through the hub's JSON protocol
      // the image would travel as base64 — a third larger and buffered as strings —
      // where a request body is a stream the relay hands to the pipe 4 KB at a time.
      //
      // XHR rather than fetch for exactly one reason: fetch still has no upload
      // progress event. Everything else about it would be nicer.
      const url =
        `/devices/${encodeURIComponent(deviceId)}/upload` + query(command, args)

      return new Promise<T>((resolve, reject) => {
        const request = new XMLHttpRequest()
        request.open("POST", url)
        request.setRequestHeader("Content-Type", "application/octet-stream")

        // Bytes accepted by the RELAY, which is not the device's write position — the
        // contract says as much. It tracks closely because the relay forwards chunk by
        // chunk while awaiting the socket, and lags at the tail while the device
        // finishes writing.
        request.upload.onprogress = (event) => {
          if (event.lengthComputable) onProgress?.(event.loaded / event.total)
        }

        request.onload = () => {
          if (request.status < 200 || request.status >= 300)
            return reject(new Error(problemDetail(request)))
          onProgress?.(1)
          try {
            resolve(request.responseText ? JSON.parse(request.responseText) : (undefined as T))
          } catch (error) {
            reject(error instanceof Error ? error : new Error(String(error)))
          }
        }
        request.onerror = () => reject(new Error("the upload could not reach the relay"))
        request.send(body)
      })
    },

    async download(
      command: string,
      args?: Record<string, unknown>,
      total?: number,
      onProgress?: (fraction: number) => void,
    ): Promise<Blob> {
      const url =
        `/devices/${encodeURIComponent(deviceId)}/download` + query(command, args)

      return new Promise<Blob>((resolve, reject) => {
        const request = new XMLHttpRequest()
        request.open("GET", url)
        request.responseType = "blob"
        request.onprogress = (event) => {
          // The relay buffers the whole reply before answering, so Content-Length is
          // known and `total` is only needed when it is not.
          const size = event.lengthComputable ? event.total : total
          if (size) onProgress?.(Math.min(1, event.loaded / size))
        }
        request.onload = () => {
          if (request.status < 200 || request.status >= 300)
            return reject(new Error(`HTTP ${request.status}`))
          onProgress?.(1)
          resolve(request.response as Blob)
        }
        request.onerror = () => reject(new Error("the download could not reach the relay"))
        request.send()
      })
    },

    logs(handler: (line: DeviceLogLine) => void): () => void {
      // The relay pushes "DeviceLog" to a per-device group; subscribing is a hub call.
      // A module cannot tell this apart from the device shell's direct read of session
      // 0, which is the point.
      const stop = on<DeviceLogPush>("DeviceLog", (push) => {
        if (push?.deviceId !== deviceId) return
        try {
          handler(JSON.parse(push.line))
        } catch {
          // Not JSON. Passed on as a bare line rather than dropped: the device wrote
          // something, and a console that hides what it cannot parse is worse than one
          // that shows it.
          handler({ log: push.line })
        }
      })
      void invoke("SubscribeDeviceLogs", deviceId).catch(() => {
        // The topbar already says when the hub is down; a module does not need to be
        // told twice, and it has no useful response to it either.
      })
      return () => {
        stop()
        void invoke("UnsubscribeDeviceLogs", deviceId).catch(() => {})
      }
    },
  }

  perDevice.set(deviceId, transport)
  return transport
}
