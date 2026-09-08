using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using StruxRelay.Devices;

namespace StruxRelay.Cache;

/// <summary>
/// Pulls a device's frontend into the cache in one go, rather than one round trip
/// per asset during somebody's first page load.
///
/// index.html first, then everything it names. Fetching the assets it *names*
/// rather than crawling the partition is what keeps this to the files that are
/// actually served, and it stays correct across a rebuild for free: a new build
/// names new files.
///
/// It uses the cache's own fetch, so there is exactly one path to the device and
/// one place that counts, and a browser arriving mid-warm shares the in-flight
/// fetch instead of starting a second one.
/// </summary>
internal sealed partial class CacheWarmer(
    FrontendCache cache,
    DeviceRegistry registry,
    ILogger<CacheWarmer> logger)
{
    /// <summary>Files named by index.html: <c>"/assets/index-D3EqElo.js"</c>.</summary>
    [GeneratedRegex("""["']\.?(/assets/[^"']+)["']""")]
    private static partial Regex AssetReference();

    private const string Index = "/index.html";

    /// <summary>
    /// Warms in the background. Its own task because it takes the pipe several
    /// times over, and the device's read loop must not wait for that.
    /// </summary>
    public void Start(DeviceConnection device) =>
        _ = Task.Run(() => WarmAsync(device, CancellationToken.None));

    public Task<WarmResult> WarmDeviceAsync(
        string deviceId, CancellationToken cancellationToken)
    {
        var device = registry.Find(deviceId);
        if (device is null || !device.Online)
            // Not an error worth logging: warming reads FROM the device, so an
            // offline one has nothing to give.
            return Task.FromResult(
                new WarmResult(false, WarmOutcome.None, 0, 0, "device is not connected"));

        return WarmAsync(device, cancellationToken);
    }

    public async Task<WarmResult> WarmAsync(
        DeviceConnection device, CancellationToken cancellationToken)
    {
        var deviceId = device.DeviceId;

        try
        {
            cache.WarmStarted(deviceId, expected: 1);

            var index = await cache.GetOrFetchAsync(device, Index, cancellationToken);
            if (index.Header.Status != 200)
            {
                var reason = $"no index.html (HTTP {index.Header.Status})";
                logger.LogInformation("cache: warm {DeviceId}: {Reason}", deviceId, reason);
                cache.WarmFinished(deviceId, WarmOutcome.Failed, 0, 1, reason);
                return new WarmResult(false, WarmOutcome.Failed, 0, 1, reason);
            }

            var paths = ReferencedAssets(index);
            var expected = paths.Count + 1;
            cache.WarmStarted(deviceId, expected);

            var warmed = 1;
            string? error = null;

            foreach (var path in paths)
            {
                if (cancellationToken.IsCancellationRequested || !device.Online)
                {
                    error = "device went away mid-warm";
                    break;
                }

                try
                {
                    var asset = await cache.GetOrFetchAsync(device, path, cancellationToken);
                    if (asset.Header.Status == 200)
                        warmed++;
                    else
                        error ??= $"{path} returned HTTP {asset.Header.Status}";
                }
                catch (Exception exception)
                {
                    // One asset failing is not the warm failing: the rest is still
                    // worth having, and the page says Partial rather than Error.
                    error ??= exception.Message;
                }
            }

            var outcome = warmed == expected ? WarmOutcome.Completed : WarmOutcome.Partial;
            cache.WarmFinished(deviceId, outcome, warmed, expected, error);

            logger.LogInformation(
                "cache: warmed {DeviceId} — {Warmed}/{Expected} files{Note}",
                deviceId, warmed, expected, error is null ? "" : $" ({error})");

            return new WarmResult(true, outcome, warmed, expected, error);
        }
        catch (Exception exception)
        {
            // Never fatal: an unwarmed cache is a slow first page load, not an
            // error, and the device is still perfectly usable.
            logger.LogInformation(
                "cache: warm {DeviceId} stopped: {Message}", deviceId, exception.Message);
            cache.WarmFinished(deviceId, WarmOutcome.Failed, 0, 0, exception.Message);
            return new WarmResult(false, WarmOutcome.Failed, 0, 0, exception.Message);
        }
    }

    /// <summary>
    /// The asset paths index.html names. The device stores its frontend gzipped
    /// and it is served through untouched, so reading the references out of it
    /// means decompressing a copy here and throwing it away.
    /// </summary>
    private static List<string> ReferencedAssets(CachedFile index)
    {
        var body = index.Body;

        if (string.Equals(index.Header.ContentEncoding, "gzip", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var compressed = new MemoryStream(body);
                using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                using var plain = new MemoryStream();
                gzip.CopyTo(plain);
                body = plain.ToArray();
            }
            catch (InvalidDataException)
            {
                // Labelled gzip but is not. Scraping the raw bytes finds nothing,
                // which leaves a warm with only index.html — Partial, and visible.
            }
        }

        var text = Encoding.UTF8.GetString(body);

        return [.. AssetReference()
            .Matches(text)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }
}

internal sealed record WarmResult(
    bool Ok, WarmOutcome Outcome, int Warmed, int Expected, string? Error);
