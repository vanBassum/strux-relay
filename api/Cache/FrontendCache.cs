using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using StruxRelay.Devices;
using StruxRelay.Models;

namespace StruxRelay.Cache;

internal readonly record struct CacheKey(string DeviceId, string Path);

internal sealed record CachedFile(WebFileHeader Header, byte[] Body, string? ETag);

internal enum WarmOutcome
{
    /// <summary>Never warmed since this device connected.</summary>
    None,

    /// <summary>index.html and everything it names are cached.</summary>
    Completed,

    /// <summary>Some of it is cached; something was missed or the pipe went away.</summary>
    Partial,

    /// <summary>Nothing usable — no index.html, or the device refused.</summary>
    Failed,
}

/// <summary>What is cached for one device, and how that came to be.</summary>
internal sealed class DeviceCacheState
{
    public int Files { get; set; }

    public long Bytes { get; set; }

    public DateTime? LastWarmedAt { get; set; }

    public DateTime? LastUsedAt { get; set; }

    public WarmOutcome LastWarm { get; set; } = WarmOutcome.None;

    public string? LastError { get; set; }

    /// <summary>Files the last warm managed, out of how many it set out to fetch.</summary>
    public int WarmedFiles { get; set; }

    public int ExpectedFiles { get; set; }
}

/// <summary>
/// (deviceId, path) → one frontend file. Bounded, least-recently-used, with
/// single-flight.
///
/// **A connection is the cache's lifetime.** Entries are dropped when a device
/// connects and kept for as long as that connection lasts — no TTL and no
/// revalidation, because the only moment a device's content can change under us
/// is one we already see: it has to reboot, and rebooting drops the pipe.
///
/// That is what keeps this entirely server-side. The alternative — the device
/// announcing a content digest so the relay could tell a flap from a reflash —
/// works, but it puts a relay's caching strategy into the firmware, and no other
/// transport has an opinion about it. What it costs is the one case connect
/// cannot see: `www` replaced on a running device without a reboot. That stays
/// cached until the device next reconnects, and Clear is the answer.
///
/// Why it exists at all: one request is in flight per device permanently, so an
/// uncached page load is N *sequential* round trips, each one an ESP32 reading
/// flash. Caching makes that happen once instead of once per view.
/// </summary>
internal sealed partial class FrontendCache(
    IOptions<CacheOptions> options,
    ILogger<FrontendCache> logger)
{
    /// <summary>
    /// A vite content-hashed name: <c>assets/&lt;name&gt;-&lt;hash&gt;.&lt;ext&gt;</c>.
    /// Used ONLY to decide what the browser is told — immutable versus
    /// revalidate. Server-side every entry lives for the connection either way.
    /// </summary>
    [GeneratedRegex(@"/assets/[^/]+-[A-Za-z0-9_-]{8,}\.[A-Za-z0-9]+$")]
    private static partial Regex ImmutableName();

    private readonly long maxBytes = options.Value.MaxBytes;
    private readonly object gate = new();

    private readonly Dictionary<CacheKey, Slot> slots = [];

    /// <summary>Front is least recently used, which is what gets evicted.</summary>
    private readonly LinkedList<CacheKey> recency = new();

    private readonly Dictionary<CacheKey, Task<CachedFile>> inflight = [];
    private readonly Dictionary<string, DeviceCacheState> devices = [];

    private long bytes;
    private long hits;
    private long misses;
    private long bytesServed;
    private long bytesFetched;
    private long failedFetches;

    private sealed class Slot
    {
        public required CachedFile File { get; init; }

        public required LinkedListNode<CacheKey> Recency { get; init; }
    }

    public long MaxBytes => maxBytes;

    public static bool IsImmutable(string path) => ImmutableName().IsMatch(path);

    // ── reading ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The cached file for this path, fetching it off the device at most once.
    ///
    /// Two browsers asking for the same uncached file would otherwise take the
    /// pipe twice in a row for identical bytes, and with one request in flight
    /// per device that is not a small waste.
    /// </summary>
    public async Task<CachedFile> GetOrFetchAsync(
        DeviceConnection device, string path, CancellationToken cancellationToken)
    {
        var key = new CacheKey(device.DeviceId, path);

        Task<CachedFile> fetch;
        var mine = false;

        lock (gate)
        {
            if (slots.TryGetValue(key, out var slot))
            {
                recency.Remove(slot.Recency);
                recency.AddLast(slot.Recency);
                hits++;
                bytesServed += slot.File.Body.Length;
                Touch(device.DeviceId, used: true);
                return slot.File;
            }

            if (!inflight.TryGetValue(key, out var existing))
            {
                var pending = new TaskCompletionSource<CachedFile>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                inflight[key] = pending.Task;
                fetch = pending.Task;
                mine = true;
                misses++;

                // Started outside the lock, below.
                _ = RunFetchAsync(device, key, path, pending);
            }
            else
            {
                fetch = existing;
            }
        }

        // The waiter's own token must not cancel a fetch others are awaiting, so
        // only the wait is cancellable — the fetch itself runs to its own
        // conclusion under the pipe's idle timeout.
        return mine
            ? await fetch
            : await fetch.WaitAsync(cancellationToken);
    }

    private async Task RunFetchAsync(
        DeviceConnection device,
        CacheKey key,
        string path,
        TaskCompletionSource<CachedFile> pending)
    {
        try
        {
            // CancellationToken.None on purpose: see GetOrFetchAsync. WebReadAsync
            // has its own silence deadline, so this cannot hang for ever.
            var file = await device.WebReadAsync(path, CancellationToken.None);

            var etag = Convert.ToHexStringLower(SHA256.HashData(file.Body))[..16];
            var cached = new CachedFile(file.Header, file.Body, $"\"{etag}\"");

            lock (gate)
            {
                bytesFetched += file.Body.Length;

                // Only 200s are cached. A 404 is cheap, and caching one would pin
                // a mistake for as long as the entry lives.
                if (file.Header.Status == 200)
                    Store(key, cached);
            }

            pending.SetResult(cached);
        }
        catch (Exception exception)
        {
            lock (gate)
            {
                failedFetches++;
                var state = State(key.DeviceId);
                state.LastError = exception.Message;
            }
            logger.LogDebug(
                "cache: fetching {Path} from {DeviceId} failed ({Message})",
                path, key.DeviceId, exception.Message);
            pending.SetException(exception);
        }
        finally
        {
            lock (gate)
                inflight.Remove(key);
        }
    }

    /// <summary>Called under the lock.</summary>
    private void Store(CacheKey key, CachedFile file)
    {
        // A single file larger than the whole budget would evict everything and
        // then itself; refuse to store it rather than thrash.
        if (file.Body.Length > maxBytes)
        {
            logger.LogWarning(
                "cache: {Path} from {DeviceId} is larger than the whole budget — not stored",
                key.Path, key.DeviceId);
            return;
        }

        DropLocked(key);

        var node = recency.AddLast(key);
        slots[key] = new Slot { File = file, Recency = node };
        bytes += file.Body.Length;

        var state = State(key.DeviceId);
        state.Files++;
        state.Bytes += file.Body.Length;

        while (bytes > maxBytes && recency.First is not null)
            DropLocked(recency.First.Value);
    }

    /// <summary>Called under the lock.</summary>
    private void DropLocked(CacheKey key)
    {
        if (!slots.Remove(key, out var slot))
            return;

        recency.Remove(slot.Recency);
        bytes -= slot.File.Body.Length;

        if (devices.TryGetValue(key.DeviceId, out var state))
        {
            state.Files--;
            state.Bytes -= slot.File.Body.Length;
        }
    }

    // ── management ────────────────────────────────────────────────────────────

    /// <summary>
    /// Everything cached for one device goes. This is what a connect calls, and
    /// what Clear calls — the same operation, because a reconnect and an operator
    /// saying "throw it away" mean exactly the same thing to the cache.
    /// </summary>
    public int DropDevice(string deviceId)
    {
        lock (gate)
            return DropDeviceLocked(deviceId);
    }

    /// <summary>Called under the lock.</summary>
    private int DropDeviceLocked(string deviceId)
    {
        var keys = slots.Keys.Where(key => key.DeviceId == deviceId).ToArray();
        foreach (var key in keys)
            DropLocked(key);

        if (devices.TryGetValue(deviceId, out var state))
        {
            state.Files = 0;
            state.Bytes = 0;
            state.LastWarm = WarmOutcome.None;
            state.LastWarmedAt = null;
            state.WarmedFiles = 0;
            state.ExpectedFiles = 0;
            state.LastError = null;
        }

        return keys.Length;
    }

    /// <summary>
    /// Drops every device's files. The counters are left alone — they are the
    /// record of what happened, not a description of what is held.
    /// </summary>
    public int Clear()
    {
        lock (gate)
        {
            var count = slots.Count;
            // Per device, so each one's warm history is reset too. Dropping the
            // files without it left a row reading "Empty" while still claiming a
            // completed warm of three files.
            foreach (var deviceId in devices.Keys.ToArray())
                DropDeviceLocked(deviceId);

            // Anything left belongs to a device with no state entry at all.
            foreach (var key in slots.Keys.ToArray())
                DropLocked(key);

            return count;
        }
    }

    /// <summary>Forgets a device entirely, files and history, when it is no longer approved.</summary>
    public void Forget(string deviceId)
    {
        lock (gate)
        {
            DropDeviceLocked(deviceId);
            devices.Remove(deviceId);
        }
    }

    // ── warming's bookkeeping ─────────────────────────────────────────────────

    public void WarmStarted(string deviceId, int expected)
    {
        lock (gate)
        {
            var state = State(deviceId);
            state.ExpectedFiles = expected;
            state.WarmedFiles = 0;
            state.LastError = null;
        }
    }

    public void WarmFinished(
        string deviceId, WarmOutcome outcome, int warmed, int expected, string? error)
    {
        lock (gate)
        {
            var state = State(deviceId);
            state.LastWarm = outcome;
            state.LastWarmedAt = DateTime.UtcNow;
            state.WarmedFiles = warmed;
            state.ExpectedFiles = expected;
            state.LastError = error;
        }
    }

    // ── statistics ────────────────────────────────────────────────────────────

    public CacheSnapshot Snapshot()
    {
        lock (gate)
        {
            var perDevice = devices.ToDictionary(
                entry => entry.Key,
                entry => new DeviceCacheState
                {
                    Files = entry.Value.Files,
                    Bytes = entry.Value.Bytes,
                    LastWarmedAt = entry.Value.LastWarmedAt,
                    LastUsedAt = entry.Value.LastUsedAt,
                    LastWarm = entry.Value.LastWarm,
                    LastError = entry.Value.LastError,
                    WarmedFiles = entry.Value.WarmedFiles,
                    ExpectedFiles = entry.Value.ExpectedFiles,
                });

            return new CacheSnapshot(
                bytes,
                maxBytes,
                slots.Count,
                devices.Values.Count(state => state.Files > 0),
                hits,
                misses,
                bytesServed,
                bytesFetched,
                failedFetches,
                perDevice);
        }
    }

    /// <summary>Called under the lock.</summary>
    private DeviceCacheState State(string deviceId)
    {
        if (!devices.TryGetValue(deviceId, out var state))
        {
            state = new DeviceCacheState();
            devices[deviceId] = state;
        }
        return state;
    }

    /// <summary>Called under the lock.</summary>
    private void Touch(string deviceId, bool used)
    {
        var state = State(deviceId);
        if (used)
            state.LastUsedAt = DateTime.UtcNow;
    }
}

internal sealed record CacheSnapshot(
    long Bytes,
    long MaxBytes,
    int Files,
    int DevicesWithContent,
    long Hits,
    long Misses,
    long BytesServed,
    long BytesFetched,
    long FailedFetches,
    IReadOnlyDictionary<string, DeviceCacheState> Devices);
