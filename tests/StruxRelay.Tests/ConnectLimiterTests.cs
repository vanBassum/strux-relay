using Microsoft.Extensions.Logging.Abstractions;
using StruxRelay.Devices;

namespace StruxRelay.Tests;

/// <summary>
/// The bound on how fast one client may get <c>/device</c> wrong. What these
/// check is mostly what the limiter must NOT do: count a device that got it
/// right, hold a failure against an address for ever, or grow without limit
/// because somebody has a lot of addresses.
/// </summary>
public class ConnectLimiterTests
{
    private const string Client = "203.0.113.7";

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    private static (ConnectLimiter Limiter, Clock Clock) New()
    {
        var clock = new Clock(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
        return (new ConnectLimiter(NullLogger<ConnectLimiter>.Instance, clock), clock);
    }

    private static void Fail(ConnectLimiter limiter, int times, string client = Client)
    {
        for (var i = 0; i < times; i++)
            limiter.RecordFailure(client);
    }

    [Fact]
    public void An_address_that_has_not_failed_is_not_limited()
    {
        var (limiter, _) = New();
        Assert.False(limiter.IsLimited(Client, out _));
    }

    [Fact]
    public void Failures_up_to_the_limit_are_allowed_through()
    {
        var (limiter, _) = New();
        Fail(limiter, ConnectLimiter.MaxFailures - 1);
        Assert.False(limiter.IsLimited(Client, out _));
    }

    [Fact]
    public void The_limit_bites_at_the_threshold_and_says_how_long()
    {
        var (limiter, _) = New();
        Fail(limiter, ConnectLimiter.MaxFailures);

        Assert.True(limiter.IsLimited(Client, out var retryAfter));
        Assert.InRange(retryAfter, 1, (int)ConnectLimiter.Window.TotalSeconds);
    }

    [Fact]
    public void The_limit_lifts_by_itself_after_the_window()
    {
        var (limiter, clock) = New();
        Fail(limiter, ConnectLimiter.MaxFailures);
        Assert.True(limiter.IsLimited(Client, out _));

        clock.Advance(ConnectLimiter.Window);

        Assert.False(limiter.IsLimited(Client, out _));
        Assert.Equal(0, limiter.Tracked);
    }

    [Fact]
    public void A_device_that_gets_it_right_clears_its_address()
    {
        var (limiter, _) = New();
        Fail(limiter, ConnectLimiter.MaxFailures);
        Assert.True(limiter.IsLimited(Client, out _));

        // The case this exists for: a good device behind the same NAT as whoever
        // is guessing. It authenticates — the pipe checks the credential even
        // while the address is limited — and the count goes with it.
        limiter.RecordSuccess(Client);

        Assert.False(limiter.IsLimited(Client, out _));
    }

    [Fact]
    public void One_address_being_limited_says_nothing_about_another()
    {
        var (limiter, _) = New();
        Fail(limiter, ConnectLimiter.MaxFailures);

        Assert.True(limiter.IsLimited(Client, out _));
        Assert.False(limiter.IsLimited("198.51.100.4", out _));
    }

    [Fact]
    public void Attempts_made_while_limited_do_not_extend_the_window()
    {
        var (limiter, clock) = New();
        Fail(limiter, ConnectLimiter.MaxFailures);

        // The pipe does not call RecordFailure while limited, so a client that
        // keeps retrying — which a refused device does, every 30 s, for ever —
        // cannot push its own window out.
        clock.Advance(ConnectLimiter.Window - TimeSpan.FromSeconds(1));
        Assert.True(limiter.IsLimited(Client, out var retryAfter));
        Assert.Equal(1, retryAfter);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(limiter.IsLimited(Client, out _));
    }

    [Fact]
    public void A_stale_count_starts_again_rather_than_accumulating()
    {
        var (limiter, clock) = New();
        Fail(limiter, ConnectLimiter.MaxFailures - 1);

        clock.Advance(ConnectLimiter.Window);
        Fail(limiter, 1);

        // One failure in this window, not MaxFailures.
        Assert.False(limiter.IsLimited(Client, out _));
    }

    [Fact]
    public void The_table_does_not_grow_without_bound()
    {
        var (limiter, _) = New();

        // An attacker with a /64 has no shortage of addresses. The table may churn;
        // it may not grow.
        for (var i = 0; i < 6000; i++)
            limiter.RecordFailure($"2001:db8::{i:x}");

        Assert.InRange(limiter.Tracked, 1, 4096);
    }

    [Fact]
    public void A_request_with_no_address_is_one_client_rather_than_none()
    {
        var (limiter, _) = New();
        Fail(limiter, ConnectLimiter.MaxFailures, client: null!);

        Assert.True(limiter.IsLimited(null, out _));
        Assert.True(limiter.IsLimited("", out _));
    }
}
