using Microsoft.Extensions.Options;
using StruxRelay.Devices;
using StruxRelay.Models;

namespace StruxRelay.Cache;

/// <summary>
/// Assembles what the Cache page shows, from the cache's own numbers and the
/// device identity the rest of the relay already has — so a row says "Strux"
/// rather than only esp32-8c4f003d3400, and says the same thing the device list
/// says.
/// </summary>
internal sealed class CacheDirectory(
    FrontendCache cache,
    DeviceDirectory directory,
    DeviceRegistry registry,
    IOptions<CacheOptions> options)
{
    public async Task<CacheView> ViewAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = cache.Snapshot();
        var known = await directory.ListAsync(cancellationToken);

        // Approved devices only. A pending device has no pipe, so it can hold no
        // cache and could never be warmed — a row for it would be a dead control.
        var rows = known
            .Where(device => device.Approval == Approval.Approved)
            .Select(device => Row(
                device.DeviceId,
                device.Name,
                device.Connection == Connection.Online,
                snapshot.Devices.GetValueOrDefault(device.DeviceId)))
            .ToList();

        // A device that was forgotten while its files were still cached would
        // otherwise hold bytes nothing on the page accounts for.
        foreach (var (deviceId, state) in snapshot.Devices)
        {
            if (state.Files == 0 || rows.Any(row => row.DeviceId == deviceId))
                continue;

            rows.Add(Row(
                deviceId,
                registry.Find(deviceId)?.Name ?? deviceId,
                registry.Find(deviceId)?.Online ?? false,
                state));
        }

        return new CacheView(
            new CacheStatsView(
                snapshot.Bytes,
                snapshot.MaxBytes,
                snapshot.Files,
                snapshot.DevicesWithContent,
                rows.Count,
                snapshot.Hits,
                snapshot.Misses,
                snapshot.BytesServed,
                snapshot.BytesFetched,
                snapshot.FailedFetches),
            Policy(snapshot.MaxBytes),
            [
                .. rows
                    .OrderByDescending(row => row.Online)
                    .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            ]);
    }

    private static DeviceCacheView Row(
        string deviceId, string name, bool online, DeviceCacheState? state)
    {
        var status = state switch
        {
            null => DeviceCacheStatus.Empty,
            { LastWarm: WarmOutcome.Failed } => DeviceCacheStatus.Error,
            { Files: 0 } => DeviceCacheStatus.Empty,
            { LastWarm: WarmOutcome.Completed } => DeviceCacheStatus.Ready,
            _ => DeviceCacheStatus.Partial,
        };

        return new DeviceCacheView(
            deviceId,
            name,
            online,
            status,
            state?.Files ?? 0,
            state?.Bytes ?? 0,
            state?.WarmedFiles ?? 0,
            state?.ExpectedFiles ?? 0,
            state?.LastWarmedAt,
            state?.LastUsedAt,
            state?.LastError);
    }

    /// <summary>
    /// The policy as it actually is. Note what is NOT here: a TTL. There is none
    /// — a connection is the cache's lifetime — and showing a plausible one would
    /// describe a cache the relay does not have.
    /// </summary>
    private CachePolicyView Policy(long maxBytes) =>
        new(
            ImmutableAssets:
                "Content-hashed /assets/… names. Browser told immutable for a year, "
                + "so a second view costs no pipe traffic at all.",
            MutableFiles:
                "index.html and anything unhashed. Browser told no-cache, so it asks "
                + "and usually gets a 304 from the ETag.",
            ServerLifetime:
                "The device's connection. Dropped when it reconnects, because that is "
                + "the only moment its content can change — no TTL and no revalidation.",
            MaxBytes: maxBytes,
            Eviction: "Least recently used, by bytes",
            WarmOnConnect: options.Value.WarmOnConnect);
}
