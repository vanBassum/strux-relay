using System.Buffers.Binary;

namespace StruxRelay.Devices;

/// <summary>
/// The device pipe's wire format: <c>[session:u16 LE][flags:u8][opaque payload]</c>.
///
/// The relay parses and REWRITES this header — it must, because it owns the
/// session-id space on the device socket — and never parses a payload. Commands,
/// the auth handshake, uploads and log lines are bytes moved between two sockets.
/// </summary>
internal static class SessionChunk
{
    public const int HeaderSize = 3;

    public const byte FlagFinal = 0x01;
    public const byte FlagReject = 0x02;

    /// <summary>
    /// Reserved by the device for its own broadcasts (log lines), so the pipe is
    /// not request/response: session 0 goes to every attached browser rather than
    /// to whoever asked.
    /// </summary>
    public const ushort BroadcastSession = 0;

    /// <summary>
    /// Also device-initiated, but consumed here instead of forwarded: Influx line
    /// protocol, one or more lines per chunk. A separate id from 0 because the two
    /// channels have different destinations — logs go to every browser,
    /// measurements go to a database — and the header is the right place to say
    /// which, rather than sniffing the payload.
    /// </summary>
    public const ushort TelemetrySession = 0xFFFF;

    // Session-id ownership. Both the relay and a browser would otherwise allocate
    // from 1 on the same device socket and collide — which presents as "the device
    // replied to the wrong request". So the relay owns the space: browser ids are
    // rewritten into the low half, the relay's own web-read sessions come from the
    // high half. The server limit stops one short of 0x10000 to keep
    // TelemetrySession unallocatable.
    public const ushort BrowserIdBase = 1;
    public const ushort BrowserIdLimit = 0x8000;
    public const ushort ServerIdBase = 0x8000;
    public const ushort ServerIdLimit = TelemetrySession;

    /// <summary>
    /// The device's inbound window. A larger frame is refused rather than split,
    /// so nothing sent to a device may exceed it.
    /// </summary>
    public const int MaxPayload = 4096;

    public static (ushort Session, byte Flags) ReadHeader(ReadOnlySpan<byte> chunk) =>
        (BinaryPrimitives.ReadUInt16LittleEndian(chunk), chunk[2]);

    public static byte[] Frame(ushort session, byte flags, ReadOnlySpan<byte> payload)
    {
        var chunk = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(chunk, session);
        chunk[2] = flags;
        payload.CopyTo(chunk.AsSpan(HeaderSize));
        return chunk;
    }

    public static bool IsTerminal(byte flags) => (flags & (FlagFinal | FlagReject)) != 0;
}

/// <summary>
/// The device answered, but not usefully: a reject, malformed, or gone.
///
/// <see cref="Refused"/> tells the two apart, and the difference is load-bearing
/// above this layer: a REFUSAL is the device declining on purpose — an unknown
/// command, a handler's own error — and for something like <c>ui modules</c> that
/// means "this firmware ships no modules", which is an ordinary answer. Anything
/// else is a fault worth showing. Only the flags on the wire can say which, so it
/// is recorded where the flags are read rather than guessed from the message text
/// later.
/// </summary>
internal sealed class RelayException(string message, bool refused = false) : Exception(message)
{
    public bool Refused { get; } = refused;
}
