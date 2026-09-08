using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using StruxRelay.Hubs;
using StruxRelay.Models;

namespace StruxRelay.Telemetry;

/// <summary>
/// Where device telemetry arrives, and the only thing that knows about counters,
/// batching and the live diagnostics feed. It does not know what a sink is made
/// of — no Influx vocabulary appears here, which is the property that lets a
/// second sink be added without touching ingestion.
///
/// The relay is not a telemetry store, and that shapes everything below: the
/// queue is bounded and overflow is counted rather than kept, the counters are in
/// memory, and nothing is buffered for a browser that is not currently watching.
/// A point that cannot be forwarded is a gap in a graph, not a claim on the
/// relay's memory.
/// </summary>
internal sealed class TelemetryRouter : BackgroundService
{
    /// <summary>
    /// How often the loop wakes. Fast enough that the live table feels immediate,
    /// slow enough that a burst becomes one push instead of hundreds.
    /// </summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(200);

    /// <summary>The status card's numbers move constantly, so they are pushed rather than polled.</summary>
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A cap on how many events one push carries. Display only: the excess is
    /// still forwarded and still counted, it simply does not appear in the table,
    /// because a page that is metres behind is less useful than one that is
    /// current.
    /// </summary>
    private const int MaxEventsPerPush = 200;

    private readonly ITelemetrySink sink;
    private readonly IHubContext<RelayHub> hub;
    private readonly ILogger<TelemetryRouter> logger;
    private readonly TelemetryOptions options;

    private readonly Channel<TelemetryPoint> queue;
    private readonly List<TelemetryPoint> pendingDisplay = [];
    private readonly object displayGate = new();

    private long sequence;
    private bool noSinkNoticed;

    public TelemetryRouter(
        ITelemetrySink sink,
        IOptions<TelemetryOptions> options,
        IHubContext<RelayHub> hub,
        ILogger<TelemetryRouter> logger)
    {
        this.sink = sink;
        this.hub = hub;
        this.logger = logger;
        this.options = options.Value;

        // DropWrite rather than Wait: the caller is a device's read loop, and
        // blocking it on a slow sink would stall the pipe that carries commands.
        queue = Channel.CreateBounded<TelemetryPoint>(
            new BoundedChannelOptions(this.options.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
            });
    }

    public TelemetryCounters Counters { get; } = new();

    public SinkStatus SinkStatus => sink.Status;

    // ── ingestion ─────────────────────────────────────────────────────────────

    /// <summary>
    /// One telemetry chunk off a device's pipe: one or more line-protocol lines,
    /// newline separated. The firmware sends one line per chunk today; splitting
    /// anyway costs nothing and means a batching firmware would not break this.
    ///
    /// Device identity comes from the CONNECTION, not from the point's own
    /// `device` tag. The relay does not verify that tag, so an approved device
    /// could attribute a point to another one — for the sink that is a known
    /// limitation, but the diagnostics page must show who actually sent it.
    /// </summary>
    public void Ingest(string deviceId, string deviceName, ReadOnlySpan<byte> payload)
    {
        var text = Encoding.UTF8.GetString(payload);
        var received = 0;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            received++;

            if (!LineProtocol.TryRead(line, out var measurement, out var tags, out var fields))
            {
                Counters.Received(1);
                Counters.Dropped(DropReason.InvalidEvent);
                logger.LogDebug(
                    "telemetry: unreadable line from {DeviceId} (dropped)", deviceId);
                continue;
            }

            // The device tag is shown in its own column, so it is taken out of the
            // tags rather than repeated beside them.
            LineProtocol.TakeTag(ref tags, "device");

            var point = new TelemetryPoint(
                Interlocked.Increment(ref sequence),
                DateTime.UtcNow,
                deviceId,
                deviceName,
                line,
                measurement,
                fields,
                tags);

            Counters.Received(1);

            // Everything received reaches the live feed, whether or not there is
            // anywhere to forward it. That is what makes this page able to prove
            // the device→relay half on its own.
            Show(point);

            if (!sink.Configured)
            {
                Counters.Dropped(DropReason.NoSinkConfigured);
                if (!noSinkNoticed)
                {
                    noSinkNoticed = true;
                    logger.LogWarning(
                        "telemetry: no sink configured — points are counted and shown, not stored");
                }
                continue;
            }

            if (!queue.Writer.TryWrite(point))
                Counters.Dropped(DropReason.QueueFull);
        }

        if (received == 0)
            logger.LogDebug("telemetry: empty chunk from {DeviceId}", deviceId);
    }

    private void Show(TelemetryPoint point)
    {
        lock (displayGate)
        {
            if (pendingDisplay.Count < MaxEventsPerPush)
                pendingDisplay.Add(point);
        }
    }

    // ── the flush loop ────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "telemetry: sink {Sink} is {State}", sink.Name, sink.Status.State);

        var batch = new List<TelemetryPoint>(options.BatchSize);
        var oldest = DateTime.UtcNow;
        var lastStatus = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Tick, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await PushEventsAsync(stoppingToken);

            while (batch.Count < options.BatchSize && queue.Reader.TryRead(out var point))
            {
                if (batch.Count == 0)
                    oldest = DateTime.UtcNow;
                batch.Add(point);
            }

            var due = batch.Count >= options.BatchSize
                || (batch.Count > 0 && DateTime.UtcNow - oldest >= options.MaxBatchAge);

            if (due)
                await FlushAsync(batch, stoppingToken);

            if (DateTime.UtcNow - lastStatus >= StatusInterval)
            {
                lastStatus = DateTime.UtcNow;
                await PushStatusAsync(stoppingToken);
            }
        }

        // A last attempt on the way out, so a graceful stop does not throw away
        // what is already in hand.
        if (batch.Count > 0)
            await FlushAsync(batch, CancellationToken.None);
    }

    private async Task FlushAsync(List<TelemetryPoint> batch, CancellationToken cancellationToken)
    {
        var count = batch.Count;
        var written = await sink.WriteAsync(batch, cancellationToken);

        if (written)
            Counters.Forwarded(count);
        else
            // Not retried and not re-queued: holding a growing list while the sink
            // is down would trade a gap in a graph for the relay's memory, which
            // is the trade this whole design refuses.
            Counters.Failed(count);

        batch.Clear();
    }

    private async Task PushEventsAsync(CancellationToken cancellationToken)
    {
        TelemetryPoint[] events;
        lock (displayGate)
        {
            if (pendingDisplay.Count == 0)
                return;
            events = [.. pendingDisplay];
            pendingDisplay.Clear();
        }

        await hub.Clients.Group(RelayHub.TelemetryGroup).SendAsync(
            "TelemetryEvents",
            events.Select(TelemetryViews.Event).ToArray(),
            cancellationToken);
    }

    private Task PushStatusAsync(CancellationToken cancellationToken) =>
        hub.Clients.Group(RelayHub.TelemetryGroup).SendAsync(
            "TelemetryStatus",
            TelemetryViews.Status(sink.Status, Counters.Snapshot()),
            cancellationToken);
}
