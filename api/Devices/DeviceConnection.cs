using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using StruxRelay.Models;
using StruxRelay.Telemetry;

namespace StruxRelay.Devices;

/// <summary>One ESP32's outbound socket: id space, session map, and the in-flight gate.</summary>
internal sealed class DeviceConnection : Cache.IFrontendSource
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
    /// RelayTransport): the two have to be decided together, because whichever
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

    // ── the channels wire ─────────────────────────────────────────────────────
    //
    // Decided by the device's FIRST frame and never asked about: CONTROL means the
    // new protocol, anything else means the old one. Both are obliged to speak
    // first and they say different things, so no timeout and no negotiation.
    private SessionChunk.Wire wire = SessionChunk.Wire.Unknown;
    private ulong handshakeNonce;
    private bool channelsReady;
    private bool lowHalf;

    /// <summary>Streams the DEVICE opened at us, by channel id, named by its OPEN envelope.</summary>
    private readonly Dictionary<ushort, string> deviceStreams = [];

    /// <summary>True once the handshake has settled, or immediately on the legacy wire.</summary>
    public bool Ready => wire == SessionChunk.Wire.Legacy || channelsReady;

    public SessionChunk.Wire Protocol => wire;

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

    /// <summary>
    /// What the device said about itself on its hello, or whatever the legacy query
    /// string carried until one arrives. Replaced wholesale rather than merged: a
    /// hello is the device's complete statement about itself, and merging would leave
    /// a key alive that the firmware has stopped reporting.
    /// </summary>
    public DeviceHello Hello { get; private set; } = DeviceHello.Empty;

    // Display only, and settable because a hello arrives AFTER the socket does.
    // DeviceId is the technical identity and the thing the token proves; none of
    // these are keyed on, so they can change under a live pipe without consequence.
    public string Firmware { get; private set; }

    public string Name { get; private set; }

    public string Project { get; private set; }

    public string? Commit { get; private set; }

    /// <summary>
    /// Called once the device has said who it is, so the row can be persisted and
    /// every open device list re-read. A callback rather than a dependency because
    /// this class moves bytes: what a hello MEANS belongs to whoever accepted the
    /// pipe.
    /// </summary>
    public Func<DeviceHello, CancellationToken, Task>? OnHello { get; set; }

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
        // One receive covers a whole chunk in the ordinary case; the accumulator is
        // what handles a fragmented frame, which the socket may deliver in pieces
        // regardless of size.
        var limit = SessionChunk.MaxPayload + SessionChunk.HeaderSize;
        var buffer = ArrayPool<byte>.Shared.Rent(limit);
        var message = new ArrayBufferWriter<byte>(limit);

        // An ArrayBufferWriter GROWS, so without this the accumulator's size is
        // whatever the peer decides to send and the relay's memory is the device's
        // to spend. The rented buffer was only ever a hint; this is the limit.
        var discarding = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                if (discarding || message.WrittenCount + result.Count > limit)
                {
                    if (!discarding)
                        logger.LogWarning(
                            "device {DeviceId} chunk over this relay's {Limit}-byte "
                            + "window - discarding it, keeping the pipe",
                            DeviceId, SessionChunk.MaxPayload);
                    discarding = true;
                    message.ResetWrittenCount();
                    // Read to the end of the message anyway: stopping early would
                    // leave its tail to be read as the next chunk's header.
                    if (!result.EndOfMessage)
                        continue;
                    discarding = false;
                    continue;
                }

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

        if (wire == SessionChunk.Wire.Unknown)
        {
            wire = (flags & SessionChunk.FlagControl) != 0
                ? SessionChunk.Wire.Channels
                : SessionChunk.Wire.Legacy;

            logger.LogInformation(
                "device {DeviceId} pipe #{Pipe} speaks the {Wire} wire",
                DeviceId, Pipe, wire);
        }

        if (wire == SessionChunk.Wire.Channels)
        {
            await OnChannelFrameAsync(session, flags, payload, cancellationToken);
            return;
        }

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

        if (session == SessionChunk.HelloSession)
        {
            await ReceiveHelloAsync(payload, cancellationToken);
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
        await RouteReplyAsync(session, flags, payload, cancellationToken);
    }

    /// <summary>
    /// A reply to something the relay or a browser asked for. Identical on both
    /// wires: the relay owns the header and nothing below it, so a frame travels
    /// to whoever asked with its flags untouched -- which is why OPEN and RESET
    /// needed no handling when the channels wire arrived.
    /// </summary>
    private async Task RouteReplyAsync(
        ushort session, byte flags, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
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

    /// <summary>
    /// One frame on the channels wire. Everything a BROWSER owns falls through to
    /// the same mapping the legacy path uses -- the relay rewrites the id and
    /// forwards the flags untouched, so OPEN and RESET need no handling here at
    /// all. What is new is the handshake, and the streams the device opens for
    /// itself in place of the three reserved ids.
    /// </summary>
    private async Task OnChannelFrameAsync(
        ushort session, byte flags, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if ((flags & SessionChunk.FlagControl) != 0)
        {
            await OnControlAsync(payload, cancellationToken);
            return;
        }

        if (!channelsReady)
        {
            logger.LogWarning(
                "device {DeviceId}: channel {Session} before READY - dropped", DeviceId, session);
            return;
        }

        // A stream the device opened at us. It names itself in its envelope exactly
        // as a command does, which is the whole of what replaced BroadcastSession,
        // TelemetrySession and HelloSession.
        if ((flags & SessionChunk.FlagOpen) != 0 && !deviceStreams.ContainsKey(session)
            && !browserSessions.ContainsKey(session) && !serverSessions.ContainsKey(session))
        {
            var name = ReadEnvelopeType(payload.Span);
            if (name is "log stream" or "telemetry stream")
            {
                deviceStreams[session] = name;
                logger.LogInformation(
                    "device {DeviceId} opened {Name} on channel {Session}", DeviceId, name, session);
            }
            else
            {
                // Silence is acceptance, so a refusal has to be said.
                await SendAsync(session, SessionChunk.FlagReset,
                    Encoding.UTF8.GetBytes("unknown stream"), cancellationToken);
            }
            return;
        }

        if (deviceStreams.TryGetValue(session, out var stream))
        {
            if ((flags & SessionChunk.FlagReset) != 0)
            {
                deviceStreams.Remove(session);
                return;
            }

            if (stream == "telemetry stream")
            {
                // Handed over by identity rather than by connection: the router has
                // no business knowing what a pipe is, and the device's own `device`
                // tag is not trusted for attribution.
                telemetry.Ingest(DeviceId, Name, payload.Span);
            }
            else
            {
                // Log records go to every attached browser, on whatever channel the
                // relay opened to each of them -- the device's id means nothing in a
                // browser's id space, so this is a copy and not a forward.
                await FanoutLogAsync(payload, cancellationToken);
            }
            return;
        }

        // Anything else is an ordinary request/reply, mapped exactly as before.
        await RouteReplyAsync(session, flags, payload, cancellationToken);
    }

    private async Task OnControlAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (!SessionChunk.ReadHandshake(payload.Span, out var version, out var peerNonce))
        {
            logger.LogWarning("device {DeviceId}: short CONTROL frame", DeviceId);
            return;
        }

        if (version != SessionChunk.ProtocolVersion)
        {
            // The relay is the hub and the only participant that is easy to
            // redeploy, so tolerance belongs here -- but v1 has nothing older to be
            // tolerant of yet, and a device speaking something else is refused
            // rather than guessed at.
            logger.LogError(
                "device {DeviceId} speaks protocol {Version}, this relay speaks {Ours}",
                DeviceId, version, SessionChunk.ProtocolVersion);
            await CloseAsync();
            return;
        }

        if (peerNonce == handshakeNonce)
        {
            handshakeNonce = NextNonce();
            await SendRawAsync(SessionChunk.Handshake(handshakeNonce), cancellationToken);
            return;
        }

        lowHalf = handshakeNonce > peerNonce;
        nextServerId = lowHalf ? SessionChunk.LowBase : SessionChunk.HighBase;
        nextBrowserId = nextServerId;
        channelsReady = true;

        logger.LogInformation(
            "device {DeviceId} pipe #{Pipe} ready, relay allocates the {Half} half",
            DeviceId, Pipe, lowHalf ? "low" : "high");

        var ready = OnReady;
        if (ready is not null) await ready(cancellationToken);
    }

    /// <summary>Called once the channels handshake has settled, so the caller can ask who this is.</summary>
    public Func<CancellationToken, Task>? OnReady { get; set; }

    /// <summary>Our half of the handshake, sent the moment the socket is accepted.</summary>
    public Task SendHandshakeAsync(CancellationToken cancellationToken)
    {
        handshakeNonce = NextNonce();
        return SendRawAsync(SessionChunk.Handshake(handshakeNonce), cancellationToken);
    }

    private static ulong NextNonce()
    {
        Span<byte> b = stackalloc byte[8];
        System.Security.Cryptography.RandomNumberGenerator.Fill(b);
        return BinaryPrimitives.ReadUInt64LittleEndian(b);
    }

    private static string ReadEnvelopeType(ReadOnlySpan<byte> payload)
    {
        try
        {
            var text = Encoding.UTF8.GetString(payload).Trim();
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("type", out var type)
                ? type.GetString() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// A log record to every attached browser. Unlike the legacy fan-out this is a
    /// COPY rather than a forward: the device's channel id means nothing in a
    /// browser's id space, so each browser gets the record on the stream the relay
    /// opened to it. With nobody attached they are dropped rather than buffered --
    /// a log line nobody is watching is not owed a queue.
    /// </summary>
    private async Task FanoutLogAsync(
        ReadOnlyMemory<byte> record, CancellationToken cancellationToken)
    {
        BrowserConnection[] attached;
        lock (browserLock)
        {
            if (browsers.Count == 0) return;
            attached = [.. browsers];
        }

        foreach (var browser in attached)
        {
            var id = await browser.EnsureLogStreamAsync(cancellationToken);
            if (id is null)
            {
                DropBrowser(browser);
                continue;
            }

            if (!await browser.SendAsync(
                    SessionChunk.Frame(id.Value, 0, record.Span), cancellationToken))
                DropBrowser(browser);
        }
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

    /// <summary>
    /// Takes the device's word for what it is. Unparseable is logged and dropped, not
    /// fatal: a malformed hello costs the row its display name, and killing an
    /// otherwise healthy pipe over a cosmetic field would be the worse trade.
    ///
    /// A key the device omits CLEARS the field rather than leaving the old value, and
    /// that is the point of replacing rather than merging — a device renamed to
    /// nothing is renamed to nothing.
    /// </summary>
    private async Task ReceiveHelloAsync(
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var hello = DeviceHello.Parse(payload.Span);
        if (hello is null)
        {
            logger.LogWarning(
                "device {DeviceId} sent a hello that is not a flat JSON object", DeviceId);
            return;
        }

        Hello = hello;
        Firmware = hello.Firmware ?? "unknown";
        Name = hello.Name is { Length: > 0 } named ? named : DeviceId;
        Project = hello.Project ?? "";
        Commit = hello.Commit;

        logger.LogInformation(
            "device {DeviceId} said hello: {Name} {Project} fw {Firmware}{Commit}",
            DeviceId, Name, Project, Firmware,
            Commit is null ? "" : $" ({Commit})");

        var announce = OnHello;
        if (announce is not null) await announce(hello, cancellationToken);
    }

    // ── relay → device ────────────────────────────────────────────────────────

    /// <summary>A frame that is already framed. Only the handshake needs this.</summary>
    public async Task SendRawAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(
                frame, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
        }
        finally
        {
            sendLock.Release();
        }
    }

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

        // A BACKSTOP, not the enforcement point. BrowserPipe's receive loop cannot
        // accumulate a message this large in the first place, so it refuses one
        // before it ever gets here -- which is what production testing of v0.8.4
        // showed, after this check was written as if it were the only one. It stays
        // because this method is reachable from anywhere that holds a chunk, and a
        // public entry point validating its own precondition costs one comparison.
        //
        // Each hop owns its own framing, so the relay cannot know what the device at
        // the other end takes in one piece -- only what it is itself willing to send.
        // The browser is told on its own session id, which is the one id it holds.
        if (payload.Length > SessionChunk.MaxPayload)
        {
            logger.LogWarning(
                "browser chunk on {DeviceId} is {Length} bytes, over this relay's "
                + "{Limit}-byte window - refused", DeviceId, payload.Length,
                SessionChunk.MaxPayload);
            await browser.SendAsync(
                SessionChunk.Frame(browserSession, SessionChunk.FlagReset,
                    Encoding.UTF8.GetBytes("chunk over the relay's window")),
                cancellationToken);
            return;
        }

        ushort session;
        bool opening;
        lock (browserLock)
        {
            opening = !browserMap.TryGetValue((browser, browserSession), out session);

            // On the channels wire, "no mapping" is not the same as "new session".
            // A frame without OPEN for an id nobody holds is residue from a channel
            // that has already finished, and the device would drop it -- but opening
            // a session for it here TAKES THE PIPE, forwards something the device
            // then drops, and holds the gate until the watchdog fires fifteen
            // seconds later. This rule is the device's, applied at the relay.
            if (opening && wire == SessionChunk.Wire.Channels
                && (flags & SessionChunk.FlagOpen) == 0)
                return;

            if (opening)
            {
                session = AllocateBrowserSession();
                browserMap[(browser, browserSession)] = session;
                browserSessions[session] = (browser, browserSession);
            }
        }

        // A RESET never waits for the pipe, and this is the whole reason cancelling
        // works through a relay. It runs on the browser's READ LOOP: anything awaited
        // here stops that browser being read at all, so a RESET queued behind a gate
        // wait can never arrive -- and the thing it would have cancelled is what holds
        // the gate. The first version of this deadlocked exactly that way, until the
        // watchdog noticed fifteen seconds later.
        if (!opening && (flags & SessionChunk.FlagReset) != 0)
        {
            await SendAsync(session, flags, payload, cancellationToken);
            lock (browserLock)
            {
                browserSessions.Remove(session);
                browserMap.Remove((browser, browserSession));
            }
            ReleaseGate(session);
            return;
        }

        // The first frame of a session takes the pipe and holds it until the device
        // finals or the browser goes.
        if (opening)
        {
            // On the channels wire, refuse rather than queue. The device answers a
            // second OPEN with RESET "busy" and this makes the relay say the same
            // thing, so a browser cannot tell whether it is talking through one --
            // and, more to the point, the read loop keeps running.
            //
            // The legacy wire has no such answer, so it still waits, which is the
            // behaviour every deployed device has always seen.
            if (wire == SessionChunk.Wire.Channels)
            {
                if (!await TryAcquireGateAsync(session))
                {
                    lock (browserLock)
                    {
                        browserSessions.Remove(session);
                        browserMap.Remove((browser, browserSession));
                    }
                    await browser.SendAsync(
                        SessionChunk.Frame(browserSession, SessionChunk.FlagReset,
                            Encoding.UTF8.GetBytes("busy")),
                        cancellationToken);
                    return;
                }
            }
            else
            {
                await AcquireGateAsync(session, cancellationToken);
            }
        }

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
        var (b, l) = Halves(server: false);

        for (var attempt = 0; attempt < l - b; attempt++)
        {
            var session = nextBrowserId;
            nextBrowserId = (ushort)(session + 1 >= l ? b : session + 1);

            if (!browserSessions.ContainsKey(session) && !serverSessions.ContainsKey(session))
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
                null,
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
        var reply = await CommandBytesAsync(command, args, null, cancellationToken);

        return Encoding.UTF8.GetString(reply);
    }

    /// <summary>
    /// The same command, with its reply left as BYTES and an optional request
    /// body appended after the envelope.
    ///
    /// It exists because a device's reply is not always text. A file read, a
    /// rendered image and a firmware partition all come back through this one
    /// path, and decoding them as UTF-8 does not fail — it silently replaces
    /// every byte that is not valid UTF-8 with U+FFFD, so a PNG arrives as a PNG
    /// shaped hole. Anything that may be handed binary asks for bytes; the
    /// string overload above stays for the callers that know they asked a
    /// question with a JSON answer.
    ///
    /// <paramref name="body"/> is written into the same session straight after
    /// the envelope line, which is exactly what a device's handler reads with
    /// <c>lendInput</c>. It is split across as many chunks as the device's
    /// window needs.
    /// </summary>
    public Task<byte[]> CommandBytesAsync(
        string command,
        IReadOnlyDictionary<string, JsonElement>? args,
        ReadOnlyMemory<byte>? body,
        CancellationToken cancellationToken) =>
        RunSessionAsync(
            BuildEnvelope(command, args), body, command, MaxCommandReply, cancellationToken);

    /// <summary>
    /// The envelope line: <c>{"type":"&lt;command&gt;", …args}</c> plus a newline. The
    /// device reads the route off the first line, so the key names are the contract.
    /// </summary>
    private static byte[] BuildEnvelope(
        string command, IReadOnlyDictionary<string, JsonElement>? args)
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
                    // `type` would otherwise emit it twice and the device would read
                    // whichever came last — silently dispatching something else.
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

        if (request.Length > SessionChunk.MaxPayload)
            throw new RelayException(
                $"'{command}' arguments are {request.Length} bytes, over the device's "
                + $"{SessionChunk.MaxPayload}-byte window");

        return request;
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
        byte[] request, ReadOnlyMemory<byte>? requestBody, string what, int maxBytes,
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
        var body = new ArrayBufferWriter<byte>();
        try
        {
            await SendRequestAsync(session, request, requestBody, cancellationToken);

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

                if ((chunk.Flags & SessionChunk.FlagReset) != 0)
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
    /// Writes one request into a session: the envelope line, then the body, in
    /// as many chunks as the device's window needs, FINAL on the last.
    ///
    /// The device has always been able to READ a request this way — its Session
    /// pulls further chunks off the link until one carries FINAL, which is how
    /// a browser has uploaded firmware all along. The relay was the half that
    /// could only ever send one chunk, which is why a command taking a body was
    /// reachable from a browser and not from here.
    ///
    /// The envelope is never split: it is capped well under the window by
    /// BuildEnvelope, and a device routes a request by finding the newline in
    /// its FIRST chunk. The body is split freely — it is a byte stream to the
    /// handler, and a chunk boundary inside it means nothing.
    /// </summary>
    private async Task SendRequestAsync(
        ushort session, byte[] envelope, ReadOnlyMemory<byte>? requestBody,
        CancellationToken cancellationToken)
    {
        var bodyBytes = requestBody ?? ReadOnlyMemory<byte>.Empty;

        // No body: one chunk, exactly as before. Worth keeping as its own case
        // so the overwhelmingly common command costs no extra send.
        if (bodyBytes.IsEmpty)
        {
            // OPEN|FINAL: the whole request in one frame, our direction closed with
            // it. The legacy wire has no OPEN and a device on it would refuse the
            // flag, so it is set only when the device said CONTROL first.
            var openFlags = wire == SessionChunk.Wire.Channels
                ? (byte)(SessionChunk.FlagOpen | SessionChunk.FlagFinal)
                : SessionChunk.FlagFinal;
            await SendAsync(session, openFlags, envelope, cancellationToken);
            return;
        }

        // The envelope shares its chunk with as much of the body as fits, so a
        // small file is still a single frame.
        var firstBodyChunk = Math.Min(
            bodyBytes.Length, SessionChunk.MaxPayload - envelope.Length);

        var first = new byte[envelope.Length + firstBodyChunk];
        envelope.CopyTo(first, 0);
        bodyBytes[..firstBodyChunk].Span.CopyTo(first.AsSpan(envelope.Length));

        var remaining = bodyBytes[firstBodyChunk..];
        await SendAsync(
            session,
            remaining.IsEmpty ? SessionChunk.FlagFinal : (byte)0,
            first,
            cancellationToken);

        while (!remaining.IsEmpty)
        {
            var take = Math.Min(remaining.Length, SessionChunk.MaxPayload);
            var chunk = remaining[..take];
            remaining = remaining[take..];

            // FINAL is what EOFs the handler's read loop, so it goes on the last
            // chunk and nowhere else. Sending it early truncates the upload; not
            // sending it at all wedges the handler until the device's own
            // receive timeout.
            await SendAsync(
                session,
                remaining.IsEmpty ? SessionChunk.FlagFinal : (byte)0,
                chunk,
                cancellationToken);
        }
    }

    /// <summary>
    /// Next free id in the relay's half of the space, wrapping. Called under the
    /// serverSessions lock.
    /// </summary>
    private ushort AllocateServerSession()
    {
        // On the channels wire the relay and its browsers share ONE half -- the one
        // the nonces gave the relay -- because the device owns the other and this
        // connection is the only place either is meaningful. On the legacy wire the
        // split is fixed and the two allocators have a half each.
        var (b, l) = Halves(server: true);

        for (var attempt = 0; attempt < l - b; attempt++)
        {
            var session = nextServerId;
            nextServerId = (ushort)(session + 1 >= l ? b : session + 1);

            if (!serverSessions.ContainsKey(session) && !browserSessions.ContainsKey(session))
                return session;
        }

        throw new RelayException("no free session ids");
    }

    /// <summary>The id range this allocator may use, by wire.</summary>
    private (int Base, int Limit) Halves(bool server)
    {
        if (wire != SessionChunk.Wire.Channels)
            return server
                ? (SessionChunk.ServerIdBase, SessionChunk.ServerIdLimit)
                : (SessionChunk.BrowserIdBase, SessionChunk.BrowserIdLimit);

        return lowHalf
            ? (SessionChunk.LowBase, SessionChunk.LowLimit)
            : (SessionChunk.HighBase, SessionChunk.HighLimit);
    }

    // ── the in-flight gate ────────────────────────────────────────────────────

    /// <summary>
    /// Take the pipe only if it is free right now. Used where blocking would stall a
    /// read loop -- see RelayFromBrowserAsync.
    /// </summary>
    public async Task<bool> TryAcquireGateAsync(ushort holder)
    {
        if (!await gate.WaitAsync(0)) return false;

        lock (gateLock)
        {
            gateHolder = holder;
            gateTouched = Stopwatch.GetTimestamp();
            gateWatchdog = new CancellationTokenSource();
            _ = WatchGateAsync(holder, gateWatchdog.Token);
        }
        return true;
    }

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
                SessionChunk.FlagReset, Encoding.UTF8.GetBytes("device disconnected")));

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
