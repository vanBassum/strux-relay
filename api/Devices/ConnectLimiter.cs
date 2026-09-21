namespace StruxRelay.Devices;

/// <summary>
/// How often one client may get <c>/device</c> wrong.
///
/// The endpoint is public by necessity — a device cannot follow a login redirect,
/// so this is the one route that cannot sit behind the proxy's forward-auth — and
/// it is the only place a token is ever checked. The pending list has been bounded
/// since pairing landed, so a stranger cannot fill the disk; what was missing is a
/// bound on how FAST an id and token can be guessed at, and on how much work a
/// stranger can make the relay do per second.
///
/// Three decisions, each of which the shape of the thing depends on:
///
/// **Keyed on the client address, never on the device id.** The id is the
/// attacker's to choose — it is a plain query parameter — so a limit keyed on it
/// is a limit the attacker resets by typing a different one. The address is the
/// one fact about a request that the sender does not get to pick. Behind the
/// reverse proxy that means the forwarded client address (see Program.cs, which
/// is also where the "this port is only reachable through that proxy" assumption
/// this rests on is written down); with a device on the LAN dialling in directly
/// it is the socket's peer.
///
/// **Only FAILURES count, and a success clears the count.** A device that
/// presents the token it was approved with is doing the one thing this endpoint
/// exists for, and the rate at which its neighbours get it wrong is not its
/// business.
///
/// **A limited client is still ANSWERED, just not written down.** Refusing
/// outright would lock out a correctly credentialed device that happens to share
/// a public address with whoever is guessing — a real arrangement, since a site's
/// boards all leave through one NAT. So while a limit is in force the credential
/// is still checked, by a read-only lookup that records nothing: no pending row,
/// no event, no attempt counter, no dashboard announcement. A good device gets in
/// and clears the count; anything else gets a 429 having cost one indexed SELECT.
///
/// In memory and per process, like the pipes themselves. A restart forgives
/// everybody, which is the same forgiveness a restart already grants a device
/// whose socket it holds.
/// </summary>
internal sealed class ConnectLimiter(ILogger<ConnectLimiter> logger, TimeProvider? clock = null)
{
    /// <summary>
    /// Failures one client may make in a window before it is limited.
    ///
    /// Sized against what honest failure looks like: a device that is not yet
    /// approved is refused and retries every 30 s — twice a minute, for as long as
    /// it takes somebody to press Approve — and a site's boards share one public
    /// address. Twenty leaves room for ten unapproved boards behind one NAT and
    /// still costs a guesser three orders of magnitude.
    /// </summary>
    public const int MaxFailures = 20;

    /// <summary>
    /// How long the count is remembered, and how long a limited client waits. One
    /// number rather than two: the count is dropped a window after the last
    /// failure that was counted, so the limit lifts by itself.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The most addresses tracked at once. The table is a public endpoint's own
    /// memory, so it is bounded like everything else here: expired entries are
    /// pruned first, and if a flood of distinct addresses still fills it the
    /// stalest one goes. An IPv6 attacker with a /64 can churn this table; what it
    /// cannot do is make it grow.
    /// </summary>
    private const int MaxTracked = 4096;

    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, Bucket> buckets = [];
    private readonly object gate = new();

    private sealed class Bucket
    {
        public int Failures;

        public DateTimeOffset LastFailure;
    }

    /// <summary>
    /// Is this client over its limit, and for how much longer. Asked BEFORE any
    /// database work, because not doing that work is most of the point.
    /// </summary>
    public bool IsLimited(string? client, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        var key = Key(client);
        var now = clock.GetUtcNow();

        lock (gate)
        {
            if (!buckets.TryGetValue(key, out var bucket)) return false;

            var elapsed = now - bucket.LastFailure;
            if (elapsed >= Window)
            {
                buckets.Remove(key);
                return false;
            }

            if (bucket.Failures < MaxFailures) return false;

            retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((Window - elapsed).TotalSeconds));
            return true;
        }
    }

    /// <summary>
    /// One refusal that was written down. Attempts made WHILE limited are not
    /// counted — they cost nothing and recording them would push the window out
    /// for as long as a device keeps retrying, which is for ever.
    /// </summary>
    public void RecordFailure(string? client)
    {
        var key = Key(client);
        var now = clock.GetUtcNow();
        var crossed = false;

        lock (gate)
        {
            if (!buckets.TryGetValue(key, out var bucket))
            {
                Prune(now);
                bucket = new Bucket();
                buckets[key] = bucket;
            }
            else if (now - bucket.LastFailure >= Window)
            {
                bucket.Failures = 0;
            }

            bucket.Failures++;
            bucket.LastFailure = now;
            crossed = bucket.Failures == MaxFailures;
        }

        if (crossed)
            logger.LogWarning(
                "{Client} has failed {Failures} device connects - refusing further "
                + "attempts from it for {Seconds}s unless they authenticate",
                key, MaxFailures, (int)Window.TotalSeconds);
    }

    /// <summary>A device that got it right. Its address is no longer suspect.</summary>
    public void RecordSuccess(string? client)
    {
        var key = Key(client);
        lock (gate)
            buckets.Remove(key);
    }

    /// <summary>Tracked addresses, for tests and for anyone wondering what it holds.</summary>
    public int Tracked
    {
        get { lock (gate) return buckets.Count; }
    }

    /// <summary>
    /// A request with no address at all — which should not happen over TCP, but is
    /// nullable in the API — counts as one client rather than as nobody. Sharing a
    /// bucket is the safe direction for something that cannot be identified.
    /// </summary>
    private static string Key(string? client) =>
        string.IsNullOrEmpty(client) ? "unknown" : client;

    /// <summary>Called under the lock, only when a new address arrives.</summary>
    private void Prune(DateTimeOffset now)
    {
        if (buckets.Count < MaxTracked) return;

        foreach (var (key, bucket) in buckets.ToArray())
            if (now - bucket.LastFailure >= Window)
                buckets.Remove(key);

        if (buckets.Count < MaxTracked) return;

        // Still full: everybody in it is recent. Drop the stalest one rather than
        // the new arrival, so the table tracks the addresses failing NOW.
        var stalest = buckets.MinBy(entry => entry.Value.LastFailure).Key;
        buckets.Remove(stalest);
        logger.LogWarning(
            "connect limiter is tracking {Count} addresses - dropping the stalest",
            MaxTracked);
    }
}
