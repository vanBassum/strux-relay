using StruxRelay.Cache;
using Microsoft.Extensions.Options;
using StruxRelay.Data;
using StruxRelay.Models;
using StruxRelay.Telemetry;

namespace StruxRelay.Devices;

/// <summary>
/// The device's outbound pipe at <c>/device</c>. The URL carries IDENTITY and nothing
/// else — <c>?id=</c> plus an <c>X-Strux-Token</c> header — because the id is the one
/// field the token proves and the one everything is keyed on. What the device is
/// CALLED, what it runs and what it was built from arrive on the socket afterwards,
/// as a hello (see <see cref="Models.DeviceHello"/>).
///
/// The old query parameters are still read when they are there, so a board in the
/// field that has not been reflashed keeps filling in a device list. That fallback is
/// the only reason this file still knows those names.
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

        // Null when the device said nothing, which is what a current firmware does —
        // NOT empty strings, which would overwrite a name the relay already has with
        // blanks on every reconnect.
        var legacy = LegacyFrom(context);
        var token = context.Request.Headers["X-Strux-Token"].ToString();
        var address = context.Connection.RemoteIpAddress?.ToString();

        // Refused BEFORE the upgrade, so the device gets an HTTP status it already
        // logs ("upgrade refused with HTTP 403") instead of a socket that opens and
        // dies. This is the check that makes the endpoint safe to leave on the
        // public internet: no stranger can register, and nobody can take an
        // approved device's slot.
        var decision = await pairing.AuthenticateAsync(
            deviceId, token, legacy, context.RequestAborted);

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
            deviceId,
            legacy?.Firmware ?? "unknown",
            legacy?.Name ?? "",
            legacy?.Project ?? "",
            address, socket, telemetry, logger);

        // What the device says about itself, once it says it. Stored and announced
        // here rather than inside the connection, which moves bytes and has no
        // business knowing there is a database.
        connection.OnHello = async (hello, cancellationToken) =>
        {
            await pairing.RecordHelloAsync(deviceId, hello, cancellationToken);
            await registry.NotifyChangedAsync();
        };

        await registry.AddAsync(connection);

        // Connect is the cache's invalidation point, and the only one it needs: a
        // device's content cannot change without a reboot, and a reboot lands
        // here. Everything cached for the old connection goes, and the new one is
        // warmed in the background — so the files are pulled once, now, instead of
        // during somebody's first page load.
        var dropped = cache.DropDevice(deviceId);

        // Deliberately says only what is known AT CONNECT. The name and version come
        // a chunk later now, and the hello logs itself when it lands.
        logger.LogInformation(
            "device {DeviceId} connected on pipe #{Pipe} from {Address}{Dropped}",
            deviceId, connection.Pipe, address,
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

    /// <summary>
    /// What an older firmware put in the connect URL, or null when it put nothing
    /// there — which is what a device that sends a hello does.
    ///
    /// Any one of the three being present is enough to call it a legacy connect: a
    /// device that names itself in the URL at all is one whose whole identity lives
    /// there, and treating a partial set as "said nothing" would drop the parts it did
    /// send.
    /// </summary>
    private static LegacyIdentity? LegacyFrom(HttpContext context)
    {
        var firmware = context.Request.Query["fw"].ToString();
        var name = context.Request.Query["name"].ToString();
        var project = context.Request.Query["project"].ToString();

        if (firmware.Length == 0 && name.Length == 0 && project.Length == 0) return null;

        return new LegacyIdentity(
            name, project, firmware.Length == 0 ? "unknown" : firmware);
    }
}
