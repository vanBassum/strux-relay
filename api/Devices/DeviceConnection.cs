using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using StruxRelay.Models;

namespace StruxRelay.Devices;

/// <summary>One ESP32's outbound socket: id space, session map, and the in-flight gate.</summary>
internal sealed class DeviceConnection
{
    /// <summary>
    /// How long a session may go SILENT before the relay gives up on it — never
    /// how long it may run. A 1 MB upload legitimately takes tens of seconds and
    /// used to have the pipe taken away mid-image, letting a concurrent request
    /// interleave into its request body; both then died. Measuring silence means a
    /// healthy upload re-arms the timer with every chunk, while a device that died
    /// still frees the pipe within this window.
    ///
    /// It is deliberately LONGER than the device's own receive timeout (10 s, in
    /// RelaySessionLink): the two have to be decided together, because whichever
    /// fires first decides how the session ends. Device first is what we want — it
    /// EOFs its own request, its handler writes a reply, and that reply releases
    /// the gate the normal way. Relay first would mean releasing the pipe while the
    /// device still believes the session is open, which is exactly the interleaving
    /// the gate exists to prevent.
    /// </summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Per-pipe serial number, for logs only. One device id can have two pipes
    /// alive at once for a moment during a reconnect, and a line that does not say
    /// which one it is about is worse than no line.
    /// </summary>
    private static int sequence;

    private readonly WebSocket socket;
    private readonly ILogger logger;

    /// <summary>The relay's own web-read calls, by the session id it minted.</summary>
    private readonly Dictionary<ushort, Channel<Chunk>> serverSessions = [];

    /// <summary>
    /// One frame per send is what the socket wants, and two tasks interleaving
    /// awaits on the same socket is not worth risking.
    /// </summary>
    private readonly SemaphoreSlim sendLock = new(1, 1);

    /// <summary>
    /// One request in flight per device. The device dispatches a chunk
    /// synchronously with no slot table, so overlapping sessions would let a file
    /// fetch's chunk land in the middle of a streamed request body. Held for a
    /// whole session, not one chunk.
    /// </summary>
    private readonly SemaphoreSlim gate = new(1, 1);

    private readonly object gateLock = new();
    private int gateHolder = -1;
    private long gateTouched;
    private CancellationTokenSource? gateWatchdog;

    private ushort nextServerId = SessionChunk.ServerIdBase;
    private bool telemetryNoticed;

    public DeviceConnection(
        string deviceId,
        string firmware,
        string name,
        string project,
        string? address,
        WebSocket socket,
        ILogger logger)
    {
        Pipe = Interlocked.Increment(ref sequence);
        DeviceId = deviceId;
        Firmware = firmware;
        // Display only. DeviceId is the technical identity and the thing the token
        // proves; these are what a human reads, so nothing is keyed on them and a
        // rename costs a device nothing.
        Name = string.IsNullOrEmpty(name) ? deviceId : name;
        Project = project;
        Address = address;
        this.socket = socket;
        this.logger = logger;
    }

    public int Pipe { get; }

    public string DeviceId { get; }

    public string Firmware { get; }

    public string Name { get; }

    public string Project { get; }

    /// <summary>Where the pipe came from. Only known while it is up.</summary>
    public string? Address { get; }

    public DateTime ConnectedAt { get; } = DateTime.UtcNow;

    /// <summary>
    /// When the device last said anything at all — a log line, a reply, a
    /// telemetry point. Distinct from <see cref="ConnectedAt"/> on purpose: a
    /// device can hold an open pipe for hours, so "how long has it been up" and
    /// "when did we last hear from it" are different questions and neither
    /// answers the other. Seeded with the connect, because the connect itself is
    /// the first thing we heard.
    /// </summary>
    public DateTime LastMessageAt { get; private set; } = DateTime.UtcNow;

    public bool Online => socket.State == WebSocketState.Open;

    private readonly record struct Chunk(byte Flags, byte[] Payload);

    // ── device → relay ────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the pipe until it closes. Runs on the request task, which is what
    /// makes the transport read on the same task that runs the command.
    /// </summary>
    public async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        // The device's window is 4096, so one receive covers a whole chunk in the
        // ordinary case; the accumulator is what handles a fragmented frame, which
        // the socket may deliver in pieces regardless of size.
        var buffer = ArrayPool<byte>.Shared.Rent(SessionChunk.MaxPayload + SessionChunk.HeaderSize);
        var message = new ArrayBufferWriter<byte>(SessionChunk.MaxPayload + SessionChunk.HeaderSize);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                message.Write(buffer.AsSpan(0, result.Count));
                if (!result.EndOfMessage)
                    continue;

                await OnChunkAsync(message.WrittenMemory, cancellationToken);
                message.ResetWrittenCount();
            }
        }
        catch (OperationCanceledException)
        {
            // A replaced pipe, or shutdown. Not an error.
        }
        catch (WebSocketException exception)
        {
            logger.LogWarning(
                "device {DeviceId} pipe #{Pipe} socket error: {Message}",
                DeviceId, Pipe, exception.Message);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task OnChunkAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
    {
        if (chunk.Length < SessionChunk.HeaderSize)
            return;

        var (session, flags) = SessionChunk.ReadHeader(chunk.Span);
        var payload = chunk[SessionChunk.HeaderSize..];

        // Before the branches: a log broadcast and a telemetry point are not
        // replies to anything, but they are still the device speaking, which is
        // the whole question this answers.
        LastMessageAt = DateTime.UtcNow;

        if (session == SessionChunk.BroadcastSession)
        {
            // Log lines, which go to every attached browser. Nothing is attached
            // until the browser pipe lands, so they are dropped rather than
            // buffered — a log line nobody is watching is not owed a queue.
            return;
        }

        if (session == SessionChunk.TelemetrySession)
        {
            if (!telemetryNoticed)
            {
                telemetryNoticed = true;
                logger.LogWarning(
                    "device {DeviceId} is sending telemetry; this relay has no sink yet, dropping",
                    DeviceId);
            }
            return;
        }

        // Device → relay counts as progress: an upload's progress reports arrive
        // this way, and so does every chunk of a long file read out of the device.
        TouchGate(session);

        Channel<Chunk>? waiting;
        lock (serverSessions)
            serverSessions.TryGetValue(session, out waiting);

        if (waiting is not null)
        {
            await waiting.Writer.WriteAsync(new Chunk(flags, payload.ToArray()), cancellationToken);
            return;
        }

        logger.LogWarning(
            "device {DeviceId}: chunk for unknown session {Session} (dropped)", DeviceId, session);
    }

    // ── relay → device ────────────────────────────────────────────────────────

    public async Task SendAsync(
        ushort session, byte flags, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(
                SessionChunk.Frame(session, flags, payload.Span),
                WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken);
        }
        finally
        {
            sendLock.Release();
        }
    }

    // ── the one command the relay issues ──────────────────────────────────────

    /// <summary>
    /// Asks the device for one frontend file. The reply is a JSON header line, a
    /// newline, then the bytes — and those bytes are passed through untouched,
    /// gzip and all, because the device owns how it stores its own frontend.
    /// </summary>
    public async Task<WebFile> WebReadAsync(string path, CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<Chunk>();
        ushort session;
        lock (serverSessions)
        {
            session = AllocateServerSession();
            serverSessions[session] = channel;
        }

        await AcquireGateAsync(session, cancellationToken);
        var body = new ArrayBufferWriter<byte>();
        try
        {
            // Written out rather than serialised from a type: the device reads the
            // envelope by name off the first line, so the key names are the
            // contract and spelling them here is what keeps them visible.
            var request = $"{{\"type\":\"web read\",\"path\":{JsonSerializer.Serialize(path)}}}\n";
            await SendAsync(
                session, SessionChunk.FlagFinal, Encoding.UTF8.GetBytes(request), cancellationToken);

            while (true)
            {
                // Per chunk, not per reply: a large file arrives as many chunks and
                // the same idleness rule applies to each wait.
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(IdleTimeout);

                Chunk chunk;
                try
                {
                    chunk = await channel.Reader.ReadAsync(idle.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new RelayException($"device {DeviceId} went silent reading {path}");
                }

                if ((chunk.Flags & SessionChunk.FlagReject) != 0)
                    throw new RelayException(
                        $"device rejected web read: {Encoding.UTF8.GetString(chunk.Payload)}");

                body.Write(chunk.Payload);
                if ((chunk.Flags & SessionChunk.FlagFinal) != 0)
                    break;
            }
        }
        finally
        {
            lock (serverSessions)
                serverSessions.Remove(session);
            ReleaseGate(session);
        }

        var reply = body.WrittenSpan;
        var newline = reply.IndexOf((byte)'\n');
        if (newline < 0)
            throw new RelayException("malformed web read reply (no header line)");

        var header = JsonSerializer.Deserialize<WebFileHeader>(reply[..newline], WebFileJson)
            ?? throw new RelayException("unparseable web read header");

        return new WebFile(header, reply[(newline + 1)..].ToArray());
    }

    private static readonly JsonSerializerOptions WebFileJson =
        new() { PropertyNameCaseInsensitive = true };


    /// <summary>
    /// Next free id in the relay's half of the space, wrapping. Called under the
    /// serverSessions lock.
    /// </summary>
    private ushort AllocateServerSession()
    {
        for (var attempt = 0; attempt < SessionChunk.ServerIdLimit - SessionChunk.ServerIdBase; attempt++)
        {
            var session = nextServerId;
            nextServerId = (ushort)(session + 1 >= SessionChunk.ServerIdLimit
                ? SessionChunk.ServerIdBase
                : session + 1);

            if (!serverSessions.ContainsKey(session))
                return session;
        }

        throw new RelayException("no free session ids");
    }

    // ── the in-flight gate ────────────────────────────────────────────────────

    public async Task AcquireGateAsync(ushort holder, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        lock (gateLock)
        {
            gateHolder = holder;
            gateTouched = Stopwatch.GetTimestamp();
            // A device that never FINALs must not wedge the pipe for good.
            gateWatchdog = new CancellationTokenSource();
            _ = WatchGateAsync(holder, gateWatchdog.Token);
        }
    }

    /// <summary>
    /// A chunk moved for this session, in either direction — it is alive. Only the
    /// holder's own traffic counts: session 0 carries log broadcasts continuously,
    /// so re-arming on any chunk at all would mean the watchdog never fires.
    /// </summary>
    public void TouchGate(ushort session)
    {
        lock (gateLock)
            if (gateHolder == session)
                gateTouched = Stopwatch.GetTimestamp();
    }

    public void ReleaseGate(ushort holder)
    {
        lock (gateLock)
        {
            if (gateHolder != holder)
                return;

            gateHolder = -1;
            gateWatchdog?.Cancel();
            gateWatchdog?.Dispose();
            gateWatchdog = null;
            gate.Release();
        }
    }

    /// <summary>
    /// Releases the pipe after <see cref="IdleTimeout"/> of silence for this
    /// session. Sleeps to the deadline as it stands, then re-checks: every chunk
    /// pushes the touch forward, so a long healthy transfer keeps extending the
    /// wait and never trips, without waking this per chunk.
    /// </summary>
    private async Task WatchGateAsync(ushort holder, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                TimeSpan remaining;
                lock (gateLock)
                    remaining = IdleTimeout - Stopwatch.GetElapsedTime(gateTouched);

                if (remaining <= TimeSpan.Zero)
                    break;

                await Task.Delay(remaining, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        logger.LogWarning(
            "device {DeviceId}: session {Holder} silent for {Seconds:0}s, releasing the pipe",
            DeviceId, holder, IdleTimeout.TotalSeconds);
        ReleaseGate(holder);
    }

    // ── teardown ──────────────────────────────────────────────────────────────

    public async Task CloseAsync()
    {
        Channel<Chunk>[] waiting;
        lock (serverSessions)
        {
            waiting = [.. serverSessions.Values];
            serverSessions.Clear();
        }

        // Everyone waiting on this pipe is told, rather than left to their idle
        // timeout: the answer is already known.
        foreach (var channel in waiting)
            channel.Writer.TryWrite(new Chunk(
                SessionChunk.FlagReject, Encoding.UTF8.GetBytes("device disconnected")));

        int holder;
        lock (gateLock)
            holder = gateHolder;
        if (holder >= 0)
            ReleaseGate((ushort)holder);

        // And the socket itself. Without this a pipe that has already been REPLACED
        // stays open until the keepalive notices, tens of seconds later — so its
        // teardown gets logged long after the new pipe is serving, which reads
        // exactly like the live device dropping. That cost an afternoon of chasing
        // a phantom reconnect loop; the device was fine the whole time.
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "replaced", CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // Already gone, which is the outcome we wanted.
        }
    }
}
