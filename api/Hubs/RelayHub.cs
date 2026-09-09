using System.Reflection;
using System.Text.Json;
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
    IHubContext<RelayHub> hub,
    ILogger<RelayHub> logger) : Hub
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

    // ── device UI modules ──────────────────────────────────────────────────

    /// <summary>
    /// One device's UI manifest — what pages and cards its firmware declares, and
    /// which bundles draw them.
    ///
    /// A dedicated method rather than <see cref="DeviceCommand"/> with
    /// <c>"ui modules"</c>, because the interesting part is the classification and
    /// only the relay can make it: a REFUSAL means this firmware ships no modules
    /// (the mixed-fleet case, and the common one), while silence or a malformed
    /// reply is a fault. Through a generic command call both arrive as "it threw",
    /// and the shell would have to guess from message text which kind of nothing it
    /// got. The hostApi range check stays in the shell, because only the shell knows
    /// what version it is.
    /// </summary>
    public async Task<DeviceUiView> GetDeviceUi(string deviceId)
    {
        var device = registry.Find(deviceId);
        if (device is null || !device.Online)
            return new DeviceUiView(UiManifestStatus.Offline, Detail: "device is not connected");

        try
        {
            var reply = await device.CommandAsync(
                "ui modules", null, Context.ConnectionAborted);
            return new DeviceUiView(UiManifestStatus.Ready, reply);
        }
        catch (RelayException exception) when (exception.Refused)
        {
            // Old firmware, or firmware that simply registers no UI. Not logged:
            // this is the ordinary answer for most of a mixed fleet.
            return new DeviceUiView(UiManifestStatus.Absent, Detail: exception.Message);
        }
        catch (Exception exception)
        {
            logger.LogInformation(
                "ui: {DeviceId} manifest read failed: {Message}", deviceId, exception.Message);
            return new DeviceUiView(UiManifestStatus.Error, Detail: exception.Message);
        }
    }

    /// <summary>
    /// Runs one command on a device and hands back its reply text.
    ///
    /// This is the relay shell's half of the module contract's
    /// <c>transport.request</c>: a module calls <c>request("led get")</c> and it
    /// arrives here. It is NOT a new transport — it is the existing
    /// <see cref="DeviceConnection"/> being asked for one more thing, over the pipe
    /// the device already dialled, through the same gate as a file read. A shell
    /// speaking the pipe itself would mean a socket per device with its own
    /// reconnect and lifecycle, reimplementing in a browser what
    /// <c>DeviceConnection</c> already is.
    ///
    /// The reply is returned unparsed, so nothing here has to know any command's
    /// shape. Every command in the device's table is reachable, which is the same
    /// reach a browser already has through <c>/devices/&lt;id&gt;/ws</c> — this is
    /// not a permission boundary and does not pretend to be one.
    ///
    /// Failure comes back as a RESULT, not as an exception. See
    /// <see cref="DeviceCommandResult"/>: SignalR rewrites a thrown exception's
    /// message, and the contract promises a module the device's own words.
    /// </summary>
    public async Task<DeviceCommandResult> DeviceCommand(
        string deviceId, string command, Dictionary<string, JsonElement>? args)
    {
        var device = registry.Find(deviceId);
        if (device is null || !device.Online)
            return new DeviceCommandResult(false, Error: $"device '{deviceId}' is not connected");

        try
        {
            var reply = await device.CommandAsync(command, args, Context.ConnectionAborted);
            return new DeviceCommandResult(true, reply);
        }
        catch (RelayException exception)
        {
            return new DeviceCommandResult(
                false, Error: exception.Message, Refused: exception.Refused);
        }
        catch (OperationCanceledException)
        {
            // The browser navigated away or closed. There is nobody left to tell.
            return new DeviceCommandResult(false, Error: "cancelled");
        }
    }

    /// <summary>Tells every open Cache page to re-read, including the one that acted.</summary>
    private Task AnnounceCacheAsync() => hub.Clients.All.SendAsync("CacheChanged");
}
