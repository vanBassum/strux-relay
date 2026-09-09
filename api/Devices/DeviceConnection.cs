using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using StruxRelay.Models;
using StruxRelay.Telemetry;

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
    private readonly TelemetryRouter telemetry;
    private readonly ILogger logger;

    /// <summary>The relay's own requests, by the session id it minted.</summary>
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

    /// <summary>
    /// The browsers watching this device, and the sessions they own. Three
    /// structures because there are three questions, all on the hot path: who is
    /// attached (the fan-out), which browser owns a device-side id (a reply coming
    /// back), and which device-side id a browser's own id was rewritten to (its
    /// next chunk). One dictionary would answer one of them by scanning.
    /// </summary>
    private readonly HashSet<BrowserConnection> browsers = [];
    private readonly Dictionary<ushort, (BrowserConnection Browser, ushort BrowserSession)> browserSessions = [];
    private readonly Dictionary<(BrowserConnection Browser, ushort BrowserSession), ushort> browserMap = [];
    private readonly object browserLock = new();

    private ushort nextBrowserId = SessionChunk.BrowserIdBase;

    public DeviceConnection(
        string deviceId,
        string firmware,
        string name,
        string project,
        string? address,
        WebSocket socket,
        TelemetryRouter telemetry,
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
        this.telemetry = telemetry;
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
            // Log lines, which go to every attached browser — verbatim, header and
            // all. Session 0 is the shape the browser expects, and rewriting it
            // into that browser's id space would turn a broadcast into a reply to a
            // request nobody made. With nobody attached they are dropped rather
            // than buffered: a log line nobody is watching is not owed a queue.
            await FanoutAsync(chunk, cancellationToken);
            return;
        }

        if (session == SessionChunk.TelemetrySession)
        {
            // Handed over by identity rather than by connection: the router has
            // no business knowing what a pipe is, and the device's own `device`
            // tag is not trusted for attribution.
            telemetry.Ingest(DeviceId, Name, payload.Span);
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

        (BrowserConnection Browser, ushort BrowserSession) owner;
        bool relayed;
        lock (browserLock)
            relayed = browserSessions.TryGetValue(session, out owner);

        if (relayed)
        {
            // Back to the id the BROWSER minted, not the one this relay rewrote it
            // to. The browser matches replies to requests by that id and has never
            // seen ours.
            var delivered = await owner.Browser.SendAsync(
                SessionChunk.Frame(owner.BrowserSession, flags, payload.Span), cancellationToken);

            if (!delivered)
            {
                // Gone mid-session. Dropping it releases every session it held,
                // rather than leaving the pipe gated until the watchdog notices —
                // and the rest of this reply has nowhere to go regardless.
                DropBrowser(owner.Browser);
                return;
            }

            if (SessionChunk.IsTerminal(flags))
                ForgetBrowserSession(session, owner);

            return;
        }

        logger.LogWarning(
            "device {DeviceId}: chunk for unknown session {Session} (dropped)", DeviceId, session);
    }

    private async Task FanoutAsync(
        ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
    {
        BrowserConnection[] attached;
        lock (browserLock)
        {
            if (browsers.Count == 0)
                return;

            attached = [.. browsers];
        }

        foreach (var browser in attached)
            if (!await browser.SendAsync(chunk, cancellationToken))
                DropBrowser(browser);
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

    // ── browser → device ──────────────────────────────────────────────────────

    public void AttachBrowser(BrowserConnection browser)
    {
        lock (browserLock)
            browsers.Add(browser);
    }

    public int BrowserCount
    {
        get { lock (browserLock) return browsers.Count; }
    }

    /// <summary>
    /// One chunk from a browser, rewritten onto the device pipe. The browser's own
    /// session id is replaced by one from the relay's browser half, because a
    /// browser and this relay would otherwise both allocate from 1 on the same
    /// socket — which presents as the device replying to the wrong request.
    /// </summary>
    public async Task RelayFromBrowserAsync(
        BrowserConnection browser, ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
    {
        if (chunk.Length < SessionChunk.HeaderSize)
            return;

        var (browserSession, flags) = SessionChunk.ReadHeader(chunk.Span);
        var payload = chunk[SessionChunk.HeaderSize..];

        ushort session;
        bool opening;
        lock (browserLock)
        {
            opening = !browserMap.TryGetValue((browser, browserSession), out session);
            if (opening)
            {
                session = AllocateBrowserSession();
                browserMap[(browser, browserSession)] = session;
                browserSessions[session] = (browser, browserSession);
            }
        }

        // The first chunk of a session takes the pipe and holds it until the device
        // finals or the browser goes. Awaited outside the lock, because what it
        // waits for is another session finishing — which can be a whole firmware
        // upload, minutes of it.
        if (opening)
            await AcquireGateAsync(session, cancellationToken);

        await SendAsync(session, flags, payload, cancellationToken);

        // Browser → device counts as progress too, and this is the direction that
        // matters most: a firmware upload is minutes of body chunks with the device
        // saying almost nothing back.
        TouchGate(session);
    }

    /// <summary>
    /// Forgets a browser and everything it was holding. Called when its socket ends
    /// and when a send to it fails — a session whose browser has gone will never be
    /// finalled, so nothing else would ever release its gate.
    /// </summary>
    public void DropBrowser(BrowserConnection browser)
    {
        List<ushort> held = [];
        lock (browserLock)
        {
            browsers.Remove(browser);

            var keys = browserMap.Keys
                .Where(key => ReferenceEquals(key.Browser, browser))
                .ToArray();

            foreach (var key in keys)
            {
                var session = browserMap[key];
                browserMap.Remove(key);
                browserSessions.Remove(session);
                held.Add(session);
            }
        }

        // Outside the lock: ReleaseGate takes gateLock, and the two are never
        // ordered the other way anywhere else.
        foreach (var session in held)
            ReleaseGate(session);
    }

    private void ForgetBrowserSession(
        ushort session, (BrowserConnection Browser, ushort BrowserSession) owner)
    {
        lock (browserLock)
        {
            browserSessions.Remove(session);
            browserMap.Remove((owner.Browser, owner.BrowserSession));
        }

        ReleaseGate(session);
    }

    /// <summary>
    /// Next free id in the browser half of the space, wrapping. Called under
    /// browserLock.
    /// </summary>
    private ushort AllocateBrowserSession()
    {
        for (var attempt = 0; attempt < SessionChunk.BrowserIdLimit - SessionChunk.BrowserIdBase; attempt++)
        {
            var session = nextBrowserId;
            nextBrowserId = (ushort)(session + 1 >= SessionChunk.BrowserIdLimit
                ? SessionChunk.BrowserIdBase
                : session + 1);

            if (!browserSessions.ContainsKey(session))
                return session;
        }

        throw new RelayException("no free browser session ids");
    }

    // ── the commands the relay issues ─────────────────────────────────────────

    /// <summary>
    /// Asks the device for one frontend file. The reply is a JSON header line, a
    /// newline, then the bytes — and those bytes are passed through untouched,
    /// gzip and all, because the device owns how it stores its own frontend.
    /// </summary>
    public async Task<WebFile> WebReadAsync(string path, CancellationToken cancellationToken)
    {
        // Written out rather than serialised from a type: the device reads the
        // envelope by name off the first line, so the key names are the contract
        // and spelling them here is what keeps them visible.
        var request = $"{{\"type\":\"web read\",\"path\":{JsonSerializer.Serialize(path)}}}\n";

        byte[] bytes;
        try
        {
            bytes = await RunSessionAsync(
                Encoding.UTF8.GetBytes(request),
                $"web read {path}",
                // A device's whole frontend lives on its own partition, so its
                // files are already bounded by something real. No relay-side
                // ceiling here.
                maxBytes: int.MaxValue,
                cancellationToken);
        }
        catch (RelayException exception)
        {
            // The bare reason is right for a command — it goes to a module's own
            // error handling — but here it ends up in a 502 body and in the cache
            // log, where "not found" without a path is no use to anybody.
            throw new RelayException(
                $"web read {path}: {exception.Message}", exception.Refused);
        }

        var reply = bytes.AsSpan();

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
    /// A reply bigger than this is refused rather than accumulated. Unlike a web
    /// read — bounded by the device's own partition — a command reply is bounded
    /// by whatever a handler decides to write, and this call is reachable from a
    /// browser. `log list` is the honest worst case at a few hundred KB.
    /// </summary>
    private const int MaxCommandReply = 1 << 20;

    /// <summary>
    /// Runs one ordinary command on the device and hands back its reply bytes,
    /// verbatim.
    ///
    /// The sibling of <see cref="WebReadAsync"/>, and deliberately nothing more
    /// than that: same gate, same session allocation, same reassembly, same idle
    /// rule. It is not a second transport — it is one more thing this connection
    /// can be asked for, which is what keeps the relay shell off the device pipe.
    ///
    /// The payload is NOT parsed here. The relay owns the session header and
    /// nothing below it (see <see cref="SessionChunk"/>), so a reply travels to
    /// whoever asked as the text the device wrote. That also means a command whose
    /// reply is not UTF-8 text is not reachable this way; the file route exists for
    /// those.
    /// </summary>
    public async Task<string> CommandAsync(
        string command,
        IReadOnlyDictionary<string, JsonElement>? args,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new RelayException("no command given");

        var envelope = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(envelope))
        {
            writer.WriteStartObject();
            writer.WriteString("type", command);

            if (args is not null)
                foreach (var (name, value) in args)
                {
                    // The route is the command, not an argument. A caller passing
                    // `type` in the args would otherwise emit it twice and the
                    // device would read whichever came last — silently dispatching
                    // something other than what was asked for.
                    if (name == "type")
                        continue;

                    writer.WritePropertyName(name);
                    value.WriteTo(writer);
                }

            writer.WriteEndObject();
        }

        var request = new byte[envelope.WrittenCount + 1];
        envelope.WrittenSpan.CopyTo(request);
        request[^1] = (byte)'\n';

        // The device refuses an oversized frame rather than splitting it, so an
        // envelope that does not fit has to fail here with a reason a caller can
        // act on instead of as a silent non-answer.
        if (request.Length > SessionChunk.MaxPayload)
            throw new RelayException(
                $"'{command}' arguments are {request.Length} bytes, over the device's "
                + $"{SessionChunk.MaxPayload}-byte window");

        var reply = await RunSessionAsync(
            request, command, MaxCommandReply, cancellationToken);

        return Encoding.UTF8.GetString(reply);
    }

    /// <summary>
    /// Writes an image to one of the device's partitions, streaming it.
    ///
    /// The one thing <see cref="CommandAsync"/> cannot express. A command is a single
    /// envelope and a single reply; this is a SESSION — an envelope chunk that is
    /// deliberately not FINAL, then the image as body chunks on the same session id,
    /// then one reply at end-of-stream. It is the same shape the device's own page
    /// uses, because it is the same handler on the far side.
    ///
    /// Three steps, and the order is load-bearing:
    ///
    /// 1. <c>partition clear</c>. `partition write` never erases, and flash bits only
    ///    clear on erase, so writing over stale content yields an image that fails
    ///    validation later — at activate, long after the upload looked fine.
    /// 2. The streamed write.
    /// 3. <c>partition activate</c>, and only once every byte landed. Until that call
    ///    the old slot still boots, so a failed upload leaves the device intact.
    ///
    /// Each step takes the gate separately rather than holding it across all three.
    /// Holding it would be easier to reason about and is not possible: the gate is not
    /// reentrant, so calling CommandAsync while holding it would deadlock against
    /// itself. The window between steps is the same one the device's own page has.
    /// </summary>
    /// <param name="progress">
    /// Device-reported bytes written — it streams <c>{"p":n}</c> as it flashes. That is
    /// its own write position rather than what has been handed to the socket, which is
    /// the only number that means anything here: the OS buffers the send, so
    /// bytes-sent races to the end while the flash write is still in flight.
    /// </param>
    public async Task<long> PartitionUploadAsync(
        string partition,
        Stream body,
        bool activate,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        var arg = new Dictionary<string, JsonElement>
        {
            ["partition"] = JsonSerializer.SerializeToElement(partition),
        };

        RequireOk(
            await CommandAsync("partition clear", arg, cancellationToken),
            $"clearing {partition}");

        var written = await StreamPartitionAsync(partition, body, progress, cancellationToken);

        if (activate)
            RequireOk(
                await CommandAsync("partition activate", arg, cancellationToken),
                $"activating {partition}");

        return written;
    }

    /// <summary>Step 2 on its own: the envelope, the body, and the one reply.</summary>
    private async Task<long> StreamPartitionAsync(
        string partition,
        Stream body,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<Chunk>();
        ushort session;
        lock (serverSessions)
        {
            session = AllocateServerSession();
            serverSessions[session] = channel;
        }

        await AcquireGateAsync(session, cancellationToken);
        var sent = 0L;
        try
        {
            var request = "{\"type\":\"partition write\",\"partition\":"
                + JsonSerializer.Serialize(partition) + "}\n";

            // NOT final: the body follows on this same session id, which is what makes
            // this a session rather than a command.
            await SendAsync(session, 0, Encoding.UTF8.GetBytes(request), cancellationToken);
            TouchGate(session);

            var buffer = ArrayPool<byte>.Shared.Rent(SessionChunk.MaxPayload);
            try
            {
                while (true)
                {
                    var read = await body.ReadAsync(
                        buffer.AsMemory(0, SessionChunk.MaxPayload), cancellationToken);
                    if (read == 0)
                        break;

                    sent += read;
                    // Every body chunk is non-final and the stream is closed by an
                    // explicit empty FINAL below, rather than by deciding which read
                    // was the last one. A request body has no length worth trusting —
                    // it may well be chunked — so "was that the end" is only
                    // answerable after the next read returns nothing.
                    await SendAsync(session, 0, buffer.AsMemory(0, read), cancellationToken);
                    TouchGate(session);
                }

                // Closes the request direction. Also the entire upload for a
                // zero-length image, which the device still has to be told about.
                await SendAsync(
                    session, SessionChunk.FlagFinal, ReadOnlyMemory<byte>.Empty, cancellationToken);
                TouchGate(session);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            // One reply at end-of-stream, preceded by however many progress records
            // the device felt like sending.
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(IdleTimeout);

                Chunk chunk;
                try
                {
                    chunk = await channel.Reader.ReadAsync(idle.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new RelayException(
                        $"device {DeviceId} went silent writing {partition} after {sent} bytes");
                }

                if ((chunk.Flags & SessionChunk.FlagReject) != 0)
                    throw new RelayException(
                        Encoding.UTF8.GetString(chunk.Payload), refused: true);

                if ((chunk.Flags & SessionChunk.FlagFinal) != 0)
                {
                    RequireOk(Encoding.UTF8.GetString(chunk.Payload), $"writing {partition}");
                    return sent;
                }

                // A progress record: the only thing on this session that is not the
                // answer. Parsed rather than forwarded, and a malformed one is
                // ignored — it is not worth failing an upload over.
                if (progress is not null)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(chunk.Payload);
                        if (doc.RootElement.TryGetProperty("p", out var p)
                            && p.TryGetInt64(out var at))
                            progress(at);
                    }
                    catch (JsonException)
                    {
                    }
                }
            }
        }
        finally
        {
            lock (serverSessions)
                serverSessions.Remove(session);
            ReleaseGate(session);
        }
    }

    /// <summary>
    /// The partition commands answer <c>{"ok":true}</c> or
    /// <c>{"ok":false,"error":…}</c> rather than refusing the session, so a failure
    /// arrives as a SUCCESSFUL reply and has to be read out of the payload. Missed,
    /// an upload reports success and the device goes on booting the old image.
    /// </summary>
    private static void RequireOk(string reply, string what)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(reply);
        }
        catch (JsonException)
        {
            throw new RelayException($"{what}: unparseable reply from the device");
        }

        using (doc)
        {
            if (doc.RootElement.TryGetProperty("ok", out var ok)
                && ok.ValueKind == JsonValueKind.False)
                throw new RelayException(
                    doc.RootElement.TryGetProperty("error", out var error)
                        ? $"{what}: {error.GetString()}"
                        : $"{what} failed");
        }
    }

    /// <summary>
    /// One request/reply session on the pipe: take the gate, send, accumulate
    /// chunks until FINAL, release. Both callers above are this plus their own
    /// reading of the bytes that come back.
    ///
    /// <paramref name="what"/> is only ever put in an error message, and exists
    /// because "the device went silent" is useless without saying what it went
    /// silent about.
    /// </summary>
    private async Task<byte[]> RunSessionAsync(
        byte[] request, string what, int maxBytes, CancellationToken cancellationToken)
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
            await SendAsync(session, SessionChunk.FlagFinal, request, cancellationToken);

            while (true)
            {
                // Per chunk, not per reply: a large reply arrives as many chunks
                // and the same idleness rule applies to each wait.
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(IdleTimeout);

                Chunk chunk;
                try
                {
                    chunk = await channel.Reader.ReadAsync(idle.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new RelayException($"device {DeviceId} went silent on {what}");
                }

                if ((chunk.Flags & SessionChunk.FlagReject) != 0)
                    // The device's OWN reason, unwrapped — a handler's RequestError
                    // arrives this way and is the most useful thing anyone gets to
                    // see about a refused command.
                    throw new RelayException(
                        Encoding.UTF8.GetString(chunk.Payload), refused: true);

                if (body.WrittenCount + chunk.Payload.Length > maxBytes)
                    throw new RelayException(
                        $"reply to {what} exceeded {maxBytes} bytes");

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

        return body.WrittenSpan.ToArray();
    }


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

        BrowserConnection[] attached;
        lock (browserLock)
        {
            attached = [.. browsers];
            browsers.Clear();
            browserSessions.Clear();
            browserMap.Clear();
        }

        // A browser attached to a pipe that has gone has nothing left to talk to,
        // and its own socket is the only way it finds that out.
        foreach (var browser in attached)
            browser.Abort();

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
        //
        // Abort, not CloseAsync. This is almost always called from a DIFFERENT task
        // than the read loop — a reconnect closing the pipe it replaced — and the
        // read loop is sitting in ReceiveAsync at that moment. Closing a socket with
        // a receive outstanding is invalid, and it threw right past the
        // WebSocketException this used to catch. Abort is synchronous, cancels the
        // pending receive, and cannot fail; a graceful close would only buy a close
        // frame for a pipe whose device has already moved to another one.
        socket.Abort();
    }
}
