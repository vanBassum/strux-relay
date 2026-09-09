using System.IO.Compression;
using System.Text;
using System.Text.Json;
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

    /// <summary>
    /// An HTML comment, so what is inside one can be dropped before scanning.
    ///
    /// Not hypothetical: Strux's index.html carries a long comment explaining its
    /// import map, and that prose contains the string <c>"./assets/x"</c> as an
    /// example. The warmer dutifully fetched <c>/assets/x</c>, got a 404, and every
    /// warm of every device came out Partial with an error naming a file that does
    /// not exist and was never meant to. A commented-out reference is not a served
    /// asset either, so dropping comments is right in general and not a workaround
    /// for one page's wording.
    /// </summary>
    [GeneratedRegex("""<!--.*?-->""", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

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
            paths.AddRange(await ModuleBundlesAsync(device, cancellationToken));
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
    /// The module bundles the device's manifest names.
    ///
    /// Asked for rather than scraped, and that is the whole reason this exists: a
    /// module bundle is named by the FIRMWARE, in a command reply, so no regex over
    /// index.html can find it. Missing them would leave the first open of a module
    /// page paying a live pipe round trip — the one thing the warmer is for.
    ///
    /// The manifest itself is deliberately not cached. It is read here and thrown
    /// away; the shell asks the device directly every time, because a stale nav is a
    /// sidebar full of pages that then fail.
    ///
    /// A device that ships no modules refuses the command, and that is the ordinary
    /// case for most of a mixed fleet — so it costs one round trip and no warning.
    /// </summary>
    private async Task<List<string>> ModuleBundlesAsync(
        DeviceConnection device, CancellationToken cancellationToken)
    {
        string reply;
        try
        {
            reply = await device.CommandAsync("ui modules", null, cancellationToken);
        }
        catch (RelayException exception) when (exception.Refused)
        {
            return [];
        }
        catch (Exception exception)
        {
            // Not the warm failing. The frontend is already warmed at this point
            // and a device page works without this; all that is lost is the head
            // start on one file.
            logger.LogInformation(
                "cache: {DeviceId} manifest read failed: {Message}",
                device.DeviceId, exception.Message);
            return [];
        }

        try
        {
            using var manifest = JsonDocument.Parse(reply);
            if (!manifest.RootElement.TryGetProperty("modules", out var modules)
                || modules.ValueKind != JsonValueKind.Array)
                return [];

            return [.. modules
                .EnumerateArray()
                .Select(module =>
                    module.TryGetProperty("entry", out var entry) && entry.ValueKind == JsonValueKind.String
                        ? entry.GetString()
                        : null)
                // Absolute paths only: the cache keys on the path the device would
                // be asked for, and a relative one would key on something no
                // request can produce.
                .Where(entry => !string.IsNullOrEmpty(entry) && entry.StartsWith('/'))
                .Select(entry => entry!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];
        }
        catch (JsonException exception)
        {
            logger.LogInformation(
                "cache: {DeviceId} sent an unparseable manifest: {Message}",
                device.DeviceId, exception.Message);
            return [];
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

        var text = HtmlComment().Replace(Encoding.UTF8.GetString(body), "");

        return [.. AssetReference()
            .Matches(text)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }
}

internal sealed record WarmResult(
    bool Ok, WarmOutcome Outcome, int Warmed, int Expected, string? Error);
