namespace StruxRelay.Telemetry;

/// <summary>
/// Bound from configuration the ordinary way, so appsettings.json, environment
/// variables and every other provider work without anything custom. The token in
/// particular belongs in an environment variable
/// (<c>Relay__Telemetry__Influx__Token</c>) rather than in a file.
/// </summary>
internal sealed class TelemetryOptions
{
    public const string Section = "Relay:Telemetry";

    /// <summary>
    /// How many points may wait for the sink. Bounded on purpose: this relay is
    /// not a telemetry store, so a sink that is down must cost a gap in a graph
    /// rather than the relay's memory. Overflow is counted as a queue-full drop.
    /// </summary>
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>Points per write. Either this or <see cref="MaxBatchAge"/> triggers a flush.</summary>
    public int BatchSize { get; set; } = 500;

    public TimeSpan MaxBatchAge { get; set; } = TimeSpan.FromSeconds(5);

    public InfluxSinkOptions Influx { get; set; } = new();
}

internal sealed class InfluxSinkOptions
{
    public string Url { get; set; } = "";

    public string Token { get; set; } = "";

    public string Org { get; set; } = "";

    public string Bucket { get; set; } = "";

    /// <summary>
    /// Long enough to ride out a slow write, short enough that a wedged Influx
    /// does not hold the flush loop forever.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Org is not required — it is optional for some Influx deployments — so it is
    /// not part of this test. A missing URL, token or bucket means there is nowhere
    /// to write, which the relay reports rather than treating as an error.
    /// </summary>
    public bool Configured =>
        Url.Length > 0 && Token.Length > 0 && Bucket.Length > 0;
}
