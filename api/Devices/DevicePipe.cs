using Microsoft.AspNetCore.SignalR;
using StruxRelay.Cache;
using Microsoft.Extensions.Options;
using StruxRelay.Data;
using StruxRelay.Hubs;
using StruxRelay.Telemetry;

namespace StruxRelay.Devices;

/// <summary>
/// The device's outbound pipe at <c>/device</c>. Registration is the query string
/// — there is no protocol verb to exchange first.
///
/// Its own path rather than the base URL, for two reasons. In production the human
/// side sits behind a reverse proxy that authenticates users, and a device cannot
/// follow a login redirect, so this is the one route that has to be excluded from
/// that. And the base URL serves the dashboard's HTML: one path meaning both would
/// be told apart only by an Upgrade header, and a misconfigured device would get
/// HTML with status 200 instead of a refusal it can report.
/// </summary>
internal static class DevicePipe
{
    public static async Task HandleAsync(
        HttpContext context,
        PairingStore pairing,
        DeviceRegistry registry,
        TelemetryRouter telemetry,
        FrontendCache cache,
        CacheWarmer warmer,
        IOptions<CacheOptions> cacheOptions,
        IHubContext<RelayHub> hub,
        ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger(typeof(DevicePipe).FullName!);

        var deviceId = context.Request.Query["id"].ToString();
        if (string.IsNullOrEmpty(deviceId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("missing ?id=");
            return;
        }

        var firmware = Query(context, "fw", "unknown");
        var name = Query(context, "name", "");
        var project = Query(context, "project", "");
        var token = context.Request.Headers["X-Strux-Token"].ToString();
        var address = context.Connection.RemoteIpAddress?.ToString();

        // Refused BEFORE the upgrade, so the device gets an HTTP status it already
        // logs ("upgrade refused with HTTP 403") instead of a socket that opens and
        // dies. This is the check that makes the endpoint safe to leave on the
        // public internet: no stranger can register, and nobody can take an
        // approved device's slot.
        var decision = await pairing.AuthenticateAsync(
            deviceId, token, name, project, firmware, context.RequestAborted);

        if (!decision.Allowed)
        {
            logger.LogWarning(
                "refused device {DeviceId} from {Address}: {Reason}",
                deviceId, address, decision.Reason);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync(decision.Reason);
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            // An approved device that asked without upgrading. 400 rather than the
            // SPA fallback's 200, which tells it nothing.
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("this endpoint is a WebSocket upgrade");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connection = new DeviceConnection(
            deviceId, firmware, name, project, address, socket, telemetry, hub, logger);

        await registry.AddAsync(connection);

        // Connect is the cache's invalidation point, and the only one it needs: a
        // device's content cannot change without a reboot, and a reboot lands
        // here. Everything cached for the old connection goes, and the new one is
        // warmed in the background — so the files are pulled once, now, instead of
        // during somebody's first page load.
        var dropped = cache.DropDevice(deviceId);

        logger.LogInformation(
            "device {DeviceId} connected on pipe #{Pipe} ({Name} fw {Firmware}) from {Address}{Dropped}",
            deviceId, connection.Pipe, string.IsNullOrEmpty(name) ? "unnamed" : name,
            firmware, address,
            dropped > 0 ? $" — dropped {dropped} cached files" : "");

        if (cacheOptions.Value.WarmOnConnect)
            warmer.Start(connection);

        try
        {
            await connection.ReadLoopAsync(context.RequestAborted);
        }
        finally
        {
            var wasCurrent = await registry.RemoveAsync(connection);
            await connection.CloseAsync();

            // CancellationToken.None deliberately: RequestAborted is ALREADY
            // cancelled by the time a dropped socket gets here, so passing it
            // would cancel the write that records the drop.
            await pairing.MarkLastSeenAsync(
                deviceId, connection.LastMessageAt, CancellationToken.None);

            logger.LogInformation(
                "device {DeviceId} pipe #{Pipe} closed{Note}",
                deviceId, connection.Pipe,
                wasCurrent ? "" : " (already replaced — device is still connected)");
        }
    }

    private static string Query(HttpContext context, string key, string fallback)
    {
        var value = context.Request.Query[key].ToString();
        return string.IsNullOrEmpty(value) ? fallback : value;
    }
}
