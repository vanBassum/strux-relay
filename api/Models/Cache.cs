using StruxRelay.Cache;

namespace StruxRelay.Models;

/// <summary>
/// What state a device's cache is in. Every one of these is derived from what the
/// cache actually knows — how many files it holds, and how the last warm ended —
/// rather than invented to give the page more colours.
/// </summary>
internal enum DeviceCacheStatus
{
    /// <summary>Nothing cached.</summary>
    Empty,

    /// <summary>index.html and every asset it names.</summary>
    Ready,

    /// <summary>Some files, but not all of what the last warm set out to fetch.</summary>
    Partial,

    /// <summary>The last warm found nothing usable.</summary>
    Error,
}

internal sealed record DeviceCacheView(
    string DeviceId,
    string Name,
    bool Online,
    DeviceCacheStatus Status,
    int Files,
    long Bytes,
    int WarmedFiles,
    int ExpectedFiles,
    DateTime? LastWarmedAt,
    DateTime? LastUsedAt,
    string? LastError);

/// <summary>
/// The cache's real policy, read off the implementation. Anything the cache does
/// not actually have — a TTL, for instance, which it does not, because a
/// connection is its lifetime — is absent rather than shown as a plausible value.
/// </summary>
internal sealed record CachePolicyView(
    string ImmutableAssets,
    string MutableFiles,
    string ServerLifetime,
    long MaxBytes,
    string Eviction,
    bool WarmOnConnect);

internal sealed record CacheStatsView(
    long Bytes,
    long MaxBytes,
    int Files,
    int DevicesWithContent,
    int DevicesKnown,
    long Hits,
    long Misses,
    long BytesServed,
    long BytesFetched,
    long FailedFetches);

internal sealed record CacheView(
    CacheStatsView Stats,
    CachePolicyView Policy,
    IReadOnlyList<DeviceCacheView> Devices);

internal sealed record CacheActionResult(bool Ok, string? Error = null, int Affected = 0);
