namespace StruxRelay.Telemetry;

internal enum SinkState
{
    /// <summary>Nowhere to write. Not an error — telemetry is optional.</summary>
    NotConfigured,

    /// <summary>Configured, but nothing has been written through it yet.</summary>
    Ready,

    /// <summary>A write has succeeded.</summary>
    Connected,

    /// <summary>The last write failed. Cleared by the next success.</summary>
    Error,
}

/// <summary>One row of non-secret configuration, for the status card.</summary>
internal sealed record SinkDetail(string Label, string Value);

internal sealed record SinkStatus(
    string Name,
    SinkState State,
    IReadOnlyList<SinkDetail> Details,
    string? LastError,
    DateTime? LastErrorAt,
    DateTime? LastWriteAt);

/// <summary>
/// Somewhere telemetry goes. InfluxDB is the first one, not the only shape this
/// is allowed to take — which is why nothing here mentions line protocol,
/// buckets or organisations, and why the router that feeds it knows none of those
/// words either.
///
/// A sink owns its own connection state and its own last error. It does not own
/// batching, queueing or counting: those are the same whatever the destination
/// is, so they live in <see cref="TelemetryRouter"/>.
/// </summary>
internal interface ITelemetrySink
{
    string Name { get; }

    /// <summary>
    /// Whether there is anywhere to write. False makes the router drop points as
    /// "no sink configured" rather than attempt a write it knows will fail.
    /// </summary>
    bool Configured { get; }

    /// <summary>
    /// A snapshot for the diagnostics page. Must never include a credential:
    /// whatever this returns is sent to a browser.
    /// </summary>
    SinkStatus Status { get; }

    /// <summary>
    /// Writes a batch, and returns whether it went. False rather than throwing,
    /// because a sink being down is an expected condition the router counts — the
    /// sink records the reason in its own status.
    /// </summary>
    Task<bool> WriteAsync(
        IReadOnlyList<TelemetryPoint> batch, CancellationToken cancellationToken);
}
