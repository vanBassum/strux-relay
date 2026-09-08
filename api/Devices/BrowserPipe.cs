using System.Buffers;
using System.Net.WebSockets;

namespace StruxRelay.Devices;

/// <summary>
/// A browser's socket at <c>/devices/{id}/ws</c>, relayed onto that device's pipe
/// with session ids rewritten into the half of the space the relay hands out.
///
/// This is the half that makes a device's OWN web UI work through the relay. The
/// page itself is served by <see cref="Cache.DeviceFrontend"/> over the same pipe,
/// and everything the page does afterwards — commands, the auth handshake, log
/// broadcasts, a firmware upload — is this socket. Nothing here reads a payload:
/// the relay is payload-opaque and owns only the three-byte header.
///
/// Kept for devices that will never ship UI modules. A shell that pulls a manifest
/// and module chunks needs the same transport underneath, so this is not a stopgap
/// to be removed once modules land.
/// </summary>
internal static class BrowserPipe
{
    public static async Task HandleAsync(
        HttpContext context,
        string deviceId,
        DeviceRegistry registry,
        ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger(typeof(BrowserPipe).FullName!);

        var connection = registry.Find(deviceId);
        if (connection is null)
        {
            // 503 rather than 404: the relay may well know this device, it simply
            // is not holding a pipe this second, and retrying is the right answer
            // to that where "no such thing" is not.
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync($"device '{deviceId}' is not connected");
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("this endpoint is a WebSocket upgrade");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var browser = new BrowserConnection(socket);
        connection.AttachBrowser(browser);

        logger.LogInformation(
            "browser attached to {DeviceId} ({Count} total)", deviceId, connection.BrowserCount);

        // Sized like the device's own window, and for the same reason: one receive
        // covers a whole chunk in the ordinary case, and the accumulator is what
        // handles a frame the socket chose to deliver in pieces.
        var buffer = ArrayPool<byte>.Shared.Rent(SessionChunk.MaxPayload + SessionChunk.HeaderSize);
        var message = new ArrayBufferWriter<byte>(SessionChunk.MaxPayload + SessionChunk.HeaderSize);

        try
        {
            while (!context.RequestAborted.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                message.Write(buffer.AsSpan(0, result.Count));
                if (!result.EndOfMessage)
                    continue;

                await connection.RelayFromBrowserAsync(
                    browser, message.WrittenMemory, context.RequestAborted);
                message.ResetWrittenCount();
            }
        }
        catch (OperationCanceledException)
        {
            // The browser navigated away, or the device pipe aborted this socket.
        }
        catch (WebSocketException exception)
        {
            logger.LogDebug(
                "browser socket on {DeviceId} ended: {Message}", deviceId, exception.Message);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);

            // Releases every session this browser was holding. Without it a tab
            // closed mid-command keeps the pipe gated until the watchdog fires.
            connection.DropBrowser(browser);
            logger.LogInformation("browser detached from {DeviceId}", deviceId);
        }
    }
}
