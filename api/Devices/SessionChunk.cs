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

    /// <summary>
    /// Terminates a channel in both directions, from either peer, at any time. It
    /// refuses an OPEN, cancels an upload, aborts a command and unsubscribes from a
    /// stream -- one flag rather than four mechanisms. Never answered with another
    /// RESET: two peers that have both forgotten a channel would trade them forever.
    /// Was FlagReject on the pre-channel wire, same bit and near enough the same
    /// meaning that the legacy path still reads it correctly.
    /// </summary>
    public const byte FlagReset = 0x02;

    /// <summary>
    /// Marks the first frame of a channel. Not negotiation -- there is no OPEN_OK
    /// and silence is acceptance -- it is what lets a receiver tell a new channel
    /// from the residue of a dead one without remembering the dead one.
    /// </summary>
    public const byte FlagOpen = 0x04;

    /// <summary>
    /// Connection-level rather than channel-level: the sender writes channel 0 and
    /// the receiver ignores it, which is why 0 stays an ordinary usable id. Its one
    /// frame in v1 is the handshake.
    /// </summary>
    public const byte FlagControl = 0x08;

    /// <summary>
    /// The handshake payload: <c>[version:u8][nonce:u64 LE]</c>. Both peers send it
    /// unprompted the moment the transport is up, so neither leads; the higher nonce
    /// takes the low half of the id space.
    /// </summary>
    public const byte ProtocolVersion = 1;
    public const int HandshakeLength = 1 + 8;

    /// <summary>
    /// Which wire a device is speaking, decided by its FIRST frame and nothing else.
    /// An old device's first act after connect is its hello on 0xFFFE; a new one's is
    /// CONTROL. Neither has to be asked and no timeout is needed, because both are
    /// obliged to speak first and they say different things.
    ///
    /// This is what makes the protocol break something each fork schedules for itself
    /// rather than a flag day across every deployed board.
    /// </summary>
    public enum Wire
    {
        Unknown,
        Legacy,
        Channels,
    }

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

    /// <summary>
    /// Device-initiated like the two above, sent once immediately after connect:
    /// what the device says about itself, as a flat key/value map (see
    /// <see cref="Models.DeviceHello"/>).
    ///
    /// A session id rather than a payload marker, for the same reason telemetry has
    /// one: the header is where a chunk says what it is, and the relay already
    /// dispatches on it without reading a byte of the body. It also means the device
    /// needs no new protocol VERB — a hello is not a command, nothing replies to it,
    /// and it is not a request the device is waiting on.
    /// </summary>
    public const ushort HelloSession = 0xFFFE;

    // Channel-id halves, for the CHANNELS wire. Which half is ours is decided per
    // connection by the handshake nonces, so unlike the legacy split below it is not
    // fixed and the "high half is the relay" reading of a log line no longer holds.
    public const ushort LowBase = 0x0000;
    public const ushort LowLimit = 0x8000;      // exclusive
    public const ushort HighBase = 0x8000;
    public const int HighLimit = 0x10000;       // exclusive

    // LEGACY session-id ownership. Both the relay and a browser would otherwise
    // allocate from 1 on the same device socket and collide — which presents as "the device
    // replied to the wrong request". So the relay owns the space: browser ids are
    // rewritten into the low half, the relay's own web-read sessions come from the
    // high half. The server limit stops one short of 0x10000 to keep
    // TelemetrySession unallocatable.
    public const ushort BrowserIdBase = 1;
    public const ushort BrowserIdLimit = 0x8000;
    public const ushort ServerIdBase = 0x8000;
    // Stops short of the reserved pair at the top, so neither can be allocated.
    public const ushort ServerIdLimit = HelloSession;

    /// <summary>
    /// How much this relay puts in ONE chunk when sending to a device, and how
    /// much it will accept in one chunk from a browser or a device.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is THIS HOP'S POLICY, not a property of the protocol and not a number
    /// the device agrees to. It used to be documented as "the device's inbound
    /// window", which was wrong in a way that mattered: it read as a shared
    /// constant that the firmware and the relay both had to hold at 4096, when
    /// what actually exists is a one-directional inequality per hop -- a sender's
    /// chunk must fit the receiver's buffer, and neither end knows the other's
    /// number.
    /// </para>
    /// <para>
    /// Nothing breaks if a device chooses a larger inbound buffer; this relay
    /// simply will not use the extra room. A device that chooses a SMALLER one
    /// refuses the chunk on that one channel and keeps its pipe, so the failure is
    /// a failed request rather than a dropped device. Raising this number is
    /// therefore a compatibility decision about the oldest firmware in the fleet,
    /// which is exactly the kind of decision that should be made here, in one
    /// place, and not inferred from a constant in another repository.
    /// </para>
    /// <para>
    /// The receive side uses the same number only because there is no reason for
    /// it to differ today. A chunk over it is refused at this boundary rather than
    /// forwarded, so one browser cannot spend the relay's memory or push an
    /// oversized chunk at a device on its own authority.
    /// </para>
    /// </remarks>
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

    public static bool IsTerminal(byte flags) => (flags & (FlagFinal | FlagReset)) != 0;

    /// <summary>The handshake frame, ready to send.</summary>
    public static byte[] Handshake(ulong nonce)
    {
        var body = new byte[HandshakeLength];
        body[0] = ProtocolVersion;
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(1), nonce);
        // Channel 0 by convention and ignored on receipt.
        return Frame(0, FlagControl, body);
    }

    /// <summary>Reads a handshake payload. False when it is too short to be one.</summary>
    public static bool ReadHandshake(ReadOnlySpan<byte> payload, out byte version, out ulong nonce)
    {
        version = 0;
        nonce = 0;
        if (payload.Length < HandshakeLength) return false;
        version = payload[0];
        nonce = BinaryPrimitives.ReadUInt64LittleEndian(payload[1..]);
        return true;
    }
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
