using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace StruxRelay.Devices;

/// <summary>
/// One browser's socket onto a device pipe. Deliberately thin: every session id
/// belongs to <see cref="DeviceConnection"/>, which owns the id space a browser's
/// ids are rewritten into, so all this adds is the send lock.
///
/// That lock is not optional. A fanned-out log line and a command reply reach the
/// same browser from two different tasks — the device's read loop is one task per
/// pipe, but the fan-out walks every browser — and a WebSocket permits exactly one
/// send at a time.
/// </summary>
internal sealed class BrowserConnection(WebSocket socket)
{
    private readonly SemaphoreSlim sendLock = new(1, 1);

    // ── the channels wire ─────────────────────────────────────────────────────
    //
    // A browser is a PEER of the relay, exactly as the device is, so this socket
    // has its own handshake and its own id halves -- entirely separate from the
    // ones on the device pipe. The relay is a channel-level proxy, not a tunnel,
    // and that was already true before the protocol said so.
    private ulong nonce;
    private bool ready;
    private bool lowHalf;
    private ushort nextId;
    private ushort? logStream;
    private readonly SemaphoreSlim streamLock = new(1, 1);

    public bool Ready => ready;

    /// <summary>Our half of the handshake, sent the moment the socket is accepted.</summary>
    public Task<bool> SendHandshakeAsync(CancellationToken cancellationToken)
    {
        Span<byte> b = stackalloc byte[8];
        RandomNumberGenerator.Fill(b);
        nonce = BinaryPrimitives.ReadUInt64LittleEndian(b);
        return SendAsync(SessionChunk.Handshake(nonce), cancellationToken);
    }

    /// <summary>
    /// The browser's CONTROL frame. True once the handshake has settled; false while
    /// a nonce collision is being redrawn, which the caller answers by sending again.
    /// </summary>
    public bool Settle(ReadOnlySpan<byte> payload, out byte version)
    {
        version = 0;
        if (!SessionChunk.ReadHandshake(payload, out version, out var peer)) return false;
        if (version != SessionChunk.ProtocolVersion) return false;
        if (peer == nonce) return false;

        lowHalf = nonce > peer;
        nextId = (ushort)(lowHalf ? SessionChunk.LowBase : SessionChunk.HighBase);
        ready = true;
        return true;
    }

    /// <summary>
    /// The channel this relay pushes log records down to this browser, opened on
    /// first use. The device's own channel id means nothing here, so records are
    /// copied onto ours rather than forwarded.
    /// </summary>
    public async Task<ushort?> EnsureLogStreamAsync(CancellationToken cancellationToken)
    {
        if (logStream is { } existing) return existing;
        if (!ready) return null;

        await streamLock.WaitAsync(cancellationToken);
        try
        {
            if (logStream is { } raced) return raced;

            var id = nextId;
            nextId = (ushort)(id + 1 >= (lowHalf ? SessionChunk.LowLimit : SessionChunk.HighLimit)
                ? (lowHalf ? SessionChunk.LowBase : SessionChunk.HighBase)
                : id + 1);

            var envelope = Encoding.UTF8.GetBytes("{\"type\":\"log stream\"}\n");
            if (!await SendAsync(SessionChunk.Frame(id, SessionChunk.FlagOpen, envelope), cancellationToken))
                return null;

            logStream = id;
            return id;
        }
        finally
        {
            streamLock.Release();
        }
    }

    public WebSocket Socket { get; } = socket;

    public bool Online => Socket.State == WebSocketState.Open;

    /// <summary>
    /// Sends one already-framed chunk, reporting failure rather than throwing.
    /// A browser closing its tab mid-broadcast is the ordinary case, and the
    /// fan-out's answer is to drop that browser and carry on with the others —
    /// which an exception per dead socket would turn into control flow.
    /// </summary>
    public async Task<bool> SendAsync(
        ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
    {
        try
        {
            await sendLock.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            if (!Online)
                return false;

            await Socket.SendAsync(
                chunk, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
            return true;
        }
        catch (Exception exception) when (
            exception is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
        finally
        {
            sendLock.Release();
        }
    }

    /// <summary>
    /// Ends this browser's socket from another task — what a device pipe closing
    /// does to the browsers hanging off it. Abort and not CloseAsync, for the same
    /// reason the device pipe aborts: the browser's own read loop is sitting in
    /// ReceiveAsync, and closing a socket with a receive outstanding is invalid.
    /// </summary>
    public void Abort()
    {
        try
        {
            Socket.Abort();
        }
        catch (ObjectDisposedException)
        {
            // Its own handler got there first. Nothing left to end.
        }
    }
}
