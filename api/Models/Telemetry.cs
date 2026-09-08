using StruxRelay.Telemetry;

namespace StruxRelay.Models;

/// <summary>
/// One point, as the diagnostics table shows it. The raw line is deliberately not
/// here: the page wants a readable measurement and payload, and shipping the line
/// as well would just be the same bytes twice.
/// </summary>
internal sealed record TelemetryEventView(
    long Sequence,
    DateTime At,
    string DeviceId,
    string DeviceName,
    string Measurement,
    string Payload,
    string Tags);

internal sealed record SinkStatusView(
    string Name,
    SinkState State,
    IReadOnlyList<SinkDetail> Details,
    string? LastError,
    DateTime? LastErrorAt,
    DateTime? LastWriteAt);

internal sealed record TelemetryCountersView(
    long Received,
    long Forwarded,
    long Dropped,
    long Failed,
    double RatePerSecond,
    DateTime? LastEventAt,
    DateTime? LastForwardAt,
    DropCounts Drops);

internal sealed record TelemetryStatusView(
    SinkStatusView Sink,
    TelemetryCountersView Counters);

internal static class TelemetryViews
{
    public static TelemetryEventView Event(TelemetryPoint point) =>
        new(
            point.Sequence,
            point.ReceivedAt,
            point.DeviceId,
            point.DeviceName,
            point.Measurement,
            point.Fields,
            point.Tags);

    public static TelemetryStatusView Status(SinkStatus sink, TelemetrySnapshot counters) =>
        new(
            new SinkStatusView(
                sink.Name,
                sink.State,
                sink.Details,
                sink.LastError,
                sink.LastErrorAt,
                sink.LastWriteAt),
            new TelemetryCountersView(
                counters.Received,
                counters.Forwarded,
                counters.Dropped,
                counters.Failed,
                counters.RatePerSecond,
                counters.LastEventAt,
                counters.LastForwardAt,
                counters.Drops));
}
