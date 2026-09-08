using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using StruxRelay.Cache;
using StruxRelay.Data;
using StruxRelay.Devices;
using StruxRelay.Models;
using StruxRelay.Telemetry;

namespace StruxRelay.Hubs;

/// <summary>
/// The only interface the relay's own dashboard has. Everything it needs is a hub
/// call or a hub push; there is no REST surface.
///
/// This is not the device pipe and shares nothing with it. A device speaks
/// [session:u16][flags:u8][payload] on a raw socket at /device, and a browser
/// speaks those same chunks to a device at /devices/{id}/ws — a protocol fixed in
/// firmware, which is why neither of those is a hub. What used to be
/// /api/devices, /api/pairing, /api/approve, /api/forget and /api/cache/flush
/// lives here instead, and the polling those needed becomes a push.
///
/// Everything here is on the browser side of the reverse proxy, so Authentik has
/// already decided who is asking. There is no additional check: whoever can open
/// the dashboard is an operator, because that is what the proxy provider grants.
/// </summary>
internal sealed class RelayHub(
    PairingStore pairing,
    DeviceDirectory directory,
    DeviceRegistry registry,
    TelemetryRouter telemetry,
    FrontendCache cache,
    CacheDirectory cacheDirectory,
    CacheWarmer warmer,
    IHubContext<RelayHub> hub) : Hub
{
    /// <summary>
    /// Who is currently watching the live telemetry feed. A group rather than
    /// broadcasting to everyone, because telemetry runs at device rate and a
    /// dashboard sitting on the device list has no use for it.
    /// </summary>
    public const string TelemetryGroup = "telemetry";

    private static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Reports the relay itself. Liveness only — a connected device is not a health
    /// condition, which is also why /healthz says nothing about them.
    /// </summary>
    public Session GetSession() => new(new Health("ok", Version));

    /// <summary>
    /// Every device the relay knows about, approved or waiting, connected or not.
    /// This is the state a dashboard catches up on when it opens; after that the
    /// pairing store pushes "PairingChanged" and the registry pushes
    /// "DevicesChanged", so nothing here is polled.
    /// </summary>
    public Task<IReadOnlyList<DeviceView>> GetDevices() =>
        directory.ListAsync(Context.ConnectionAborted);

    /// <summary>
    /// Approve one pending pair. Addressed by the PAIR and not the id, because two
    /// tokens claiming one id is what somebody guessing looks like and the operator
    /// is deciding about this exact token.
    ///
    /// Nothing is sent to the device: it is already retrying every few seconds, so
    /// the next attempt is the one that succeeds. That is the whole handshake.
    /// </summary>
    public Task<ApproveResult> Approve(string deviceId, string token) =>
        pairing.ApproveAsync(deviceId, token, Context.ConnectionAborted);

    /// <summary>
    /// Revoke a device: its approval, its pending rows, and its live pipe. The last
    /// one matters — the token is only checked when a connection is made, so a
    /// revoked device would otherwise stay connected until it happened to drop.
    ///
    /// On a device that was only pending this is a reject, and it is not a block: a
    /// refused device keeps retrying, so it will reappear as pending. Refusing for
    /// good would need a state the relay does not have yet.
    /// </summary>
    public async Task<ForgetResult> Forget(string deviceId)
    {
        var result = await pairing.ForgetAsync(deviceId, Context.ConnectionAborted);
        await registry.DropAsync(deviceId);
        return result;
    }

    /// <summary>
    /// Sink state and the in-memory counters. What a page catches up on when it
    /// opens; after that the router pushes "TelemetryStatus" about once a second,
    /// because these numbers move continuously and polling them would be silly.
    /// </summary>
    public TelemetryStatusView GetTelemetry() =>
        TelemetryViews.Status(telemetry.SinkStatus, telemetry.Counters.Snapshot());

    /// <summary>
    /// Start receiving "TelemetryEvents" pushes.
    ///
    /// Nothing is replayed, and there is nothing to replay: the relay keeps no
    /// telemetry history, so a subscriber sees what arrives from now on. The
    /// bounded list of recent events lives in the browser.
    /// </summary>
    public Task SubscribeTelemetry() =>
        Groups.AddToGroupAsync(Context.ConnectionId, TelemetryGroup);

    public Task UnsubscribeTelemetry() =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, TelemetryGroup);

    // ── the frontend cache ────────────────────────────────────────────────────

    /// <summary>
    /// Cache statistics, the real policy, and a row per approved device. Read on
    /// demand rather than pushed: unlike telemetry these numbers only move when
    /// somebody loads a device page or warms one, and "CacheChanged" covers the
    /// actions taken from here.
    /// </summary>
    public Task<CacheView> GetCache() =>
        cacheDirectory.ViewAsync(Context.ConnectionAborted);

    /// <summary>
    /// Pulls one device's frontend into the cache, through the same fetch a page
    /// load uses — there is no second downloader, so a browser arriving mid-warm
    /// shares the in-flight fetch rather than starting its own.
    /// </summary>
    public async Task<CacheActionResult> WarmDeviceCache(string deviceId)
    {
        var result = await warmer.WarmDeviceAsync(deviceId, Context.ConnectionAborted);
        await AnnounceCacheAsync();
        return new CacheActionResult(result.Ok, result.Error, result.Warmed);
    }

    public async Task<CacheActionResult> ClearDeviceCache(string deviceId)
    {
        var dropped = cache.DropDevice(deviceId);
        await AnnounceCacheAsync();
        return new CacheActionResult(true, null, dropped);
    }

    /// <summary>
    /// Warms every connected device. Sequential on purpose: each warm takes its
    /// device's pipe several times over, and there is exactly one in flight per
    /// device anyway, so running them together would only queue.
    /// </summary>
    public async Task<CacheActionResult> WarmAllCaches()
    {
        var warmed = 0;
        foreach (var device in registry.Connected())
        {
            var result = await warmer.WarmAsync(device, Context.ConnectionAborted);
            if (result.Ok)
                warmed++;
        }

        await AnnounceCacheAsync();
        return new CacheActionResult(true, null, warmed);
    }

    public async Task<CacheActionResult> ClearAllCaches()
    {
        var dropped = cache.Clear();
        await AnnounceCacheAsync();
        return new CacheActionResult(true, null, dropped);
    }

    /// <summary>Tells every open Cache page to re-read, including the one that acted.</summary>
    private Task AnnounceCacheAsync() => hub.Clients.All.SendAsync("CacheChanged");
}
