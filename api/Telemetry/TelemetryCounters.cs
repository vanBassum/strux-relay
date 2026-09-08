namespace StruxRelay.Telemetry;

/// <summary>Why a point never reached a sink. Counted separately so the page can say which.</summary>
internal enum DropReason
{
    /// <summary>Nothing is configured to write to. Expected, and not a failure.</summary>
    NoSinkConfigured,

    /// <summary>The payload did not read as a point at all.</summary>
    InvalidEvent,

    /// <summary>The queue was full — the sink is not keeping up, or is down.</summary>
    QueueFull,
}

/// <summary>
/// Operational counters, in memory and nowhere else.
///
/// Deliberately not persisted and deliberately not history: this says how the
/// relay is doing right now, and a restart resetting it is correct rather than a
/// limitation. Anything wanting the actual series should ask the sink.
/// </summary>
internal sealed class TelemetryCounters
{
    // One second per slot, and a few more than the window needs so that reading
    // and advancing cannot race into the slot being written.
    private const int Slots = 16;
    private const int RateWindowSeconds = 10;

    private readonly object gate = new();
    private readonly long[] perSecond = new long[Slots];
    private long currentSecond = Now();

    private long received;
    private long forwarded;
    private long failed;
    private long droppedNoSink;
    private long droppedInvalid;
    private long droppedQueueFull;

    private DateTime? lastEventAt;
    private DateTime? lastForwardAt;

    public void Received(int count)
    {
        Interlocked.Add(ref received, count);
        lastEventAt = DateTime.UtcNow;

        lock (gate)
        {
            Advance(Now());
            perSecond[currentSecond % Slots] += count;
        }
    }

    public void Forwarded(int count)
    {
        Interlocked.Add(ref forwarded, count);
        lastForwardAt = DateTime.UtcNow;
    }

    /// <summary>
    /// A batch the sink refused. Counted as failed rather than dropped: the
    /// distinction is whether the relay tried, and it did.
    /// </summary>
    public void Failed(int count) => Interlocked.Add(ref failed, count);

    public void Dropped(DropReason reason, int count = 1)
    {
        switch (reason)
        {
            case DropReason.NoSinkConfigured:
                Interlocked.Add(ref droppedNoSink, count);
                break;
            case DropReason.InvalidEvent:
                Interlocked.Add(ref droppedInvalid, count);
                break;
            case DropReason.QueueFull:
                Interlocked.Add(ref droppedQueueFull, count);
                break;
        }
    }

    public TelemetrySnapshot Snapshot()
    {
        double rate;
        lock (gate)
        {
            var now = Now();
            Advance(now);

            long sum = 0;
            // The current second is skipped: it is still filling, so including it
            // makes the rate read low every time it is sampled.
            for (var second = now - RateWindowSeconds; second < now; second++)
                sum += perSecond[second % Slots];

            rate = (double)sum / RateWindowSeconds;
        }

        var noSink = Interlocked.Read(ref droppedNoSink);
        var invalid = Interlocked.Read(ref droppedInvalid);
        var queueFull = Interlocked.Read(ref droppedQueueFull);

        return new TelemetrySnapshot(
            Interlocked.Read(ref received),
            Interlocked.Read(ref forwarded),
            noSink + invalid + queueFull,
            Interlocked.Read(ref failed),
            rate,
            lastEventAt,
            lastForwardAt,
            new DropCounts(noSink, invalid, queueFull));
    }

    /// <summary>Zeroes the seconds that elapsed since the last observation, under the lock.</summary>
    private void Advance(long second)
    {
        if (second == currentSecond)
            return;

        var stale = Math.Min(second - currentSecond, Slots);
        for (var s = currentSecond + 1; s <= currentSecond + stale; s++)
            perSecond[s % Slots] = 0;

        currentSecond = second;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

internal sealed record DropCounts(long NoSinkConfigured, long InvalidEvent, long QueueFull);

internal sealed record TelemetrySnapshot(
    long Received,
    long Forwarded,
    long Dropped,
    long Failed,
    double RatePerSecond,
    DateTime? LastEventAt,
    DateTime? LastForwardAt,
    DropCounts Drops);
