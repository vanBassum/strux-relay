using System.Text;
using Microsoft.Extensions.Options;

namespace StruxRelay.Telemetry;

/// <summary>
/// Writes points to InfluxDB with its line-protocol endpoint.
///
/// The bodies it sends are the device's own lines, joined with newlines and
/// otherwise untouched — the firmware already formats line protocol, so this sink
/// exists because Influx speaks the format the devices happen to emit, not
/// because the relay converts anything. A sink for something else would receive
/// the same <see cref="TelemetryPoint"/> and render it however that destination
/// wants.
/// </summary>
internal sealed class InfluxTelemetrySink(
    IOptions<TelemetryOptions> options,
    IHttpClientFactory clients,
    ILogger<InfluxTelemetrySink> logger) : ITelemetrySink
{
    private readonly InfluxSinkOptions influx = options.Value.Influx;
    private readonly object gate = new();

    private string? lastError;
    private DateTime? lastErrorAt;
    private DateTime? lastWriteAt;

    public string Name => "InfluxDB";

    public bool Configured => influx.Configured;

    public SinkStatus Status
    {
        get
        {
            lock (gate)
            {
                var state = !Configured
                    ? SinkState.NotConfigured
                    : lastError is not null
                        ? SinkState.Error
                        : lastWriteAt is null
                            ? SinkState.Ready
                            : SinkState.Connected;

                // Everything here reaches a browser, so the token is reported as
                // present or absent and never by value. A masked string would be
                // worse than this: it would suggest there is a way to reveal it.
                var details = new List<SinkDetail>
                {
                    new("URL", influx.Url.Length > 0 ? influx.Url : "—"),
                    new("Organization", influx.Org.Length > 0 ? influx.Org : "—"),
                    new("Bucket", influx.Bucket.Length > 0 ? influx.Bucket : "—"),
                    new("Token", influx.Token.Length > 0 ? "set" : "not set"),
                };

                return new SinkStatus(Name, state, details, lastError, lastErrorAt, lastWriteAt);
            }
        }
    }

    public async Task<bool> WriteAsync(
        IReadOnlyList<TelemetryPoint> batch, CancellationToken cancellationToken)
    {
        if (!Configured || batch.Count == 0)
            return false;

        // ns precision: the device sends nanoseconds when its clock is synced and
        // omits the timestamp entirely when it is not, which lets Influx stamp
        // arrival instead. Both shapes go in the same request.
        var url = $"{influx.Url.TrimEnd('/')}/api/v2/write" +
            $"?org={Uri.EscapeDataString(influx.Org)}" +
            $"&bucket={Uri.EscapeDataString(influx.Bucket)}" +
            "&precision=ns";

        var body = string.Join("\n", batch.Select(point => point.Line));

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Token {influx.Token}");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(influx.Timeout);

        try
        {
            var client = clients.CreateClient(nameof(InfluxTelemetrySink));
            using var response = await client.SendAsync(request, deadline.Token);

            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(deadline.Token);
                Fail($"HTTP {(int)response.StatusCode}: {Shorten(detail)}");
                logger.LogWarning(
                    "telemetry: Influx refused {Count} points ({Status})",
                    batch.Count, (int)response.StatusCode);
                return false;
            }

            lock (gate)
            {
                lastError = null;
                lastErrorAt = null;
                lastWriteAt = DateTime.UtcNow;
            }
            logger.LogDebug("telemetry: wrote {Count} points", batch.Count);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down. Not a sink failure, and not worth recording as one.
            return false;
        }
        catch (Exception exception)
        {
            Fail(Shorten(exception.Message));
            logger.LogWarning(
                "telemetry: write of {Count} points failed ({Message})",
                batch.Count, exception.Message);
            return false;
        }
    }

    private void Fail(string message)
    {
        lock (gate)
        {
            lastError = message;
            lastErrorAt = DateTime.UtcNow;
        }
    }

    /// <summary>Influx error bodies can be long; the card shows one line of it.</summary>
    private static string Shorten(string text)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= 200 ? single : single[..200] + "…";
    }
}
