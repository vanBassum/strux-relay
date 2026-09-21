using System.Buffers;
using System.Net.WebSockets;
using System.Text;

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

        // On the channels wire this socket is its own Connection with its own
        // handshake, unrelated to the one on the device pipe. On the legacy wire
        // there is no handshake and the browser is marked ready by fiat below.
        if (connection.Protocol == SessionChunk.Wire.Channels)
            await browser.SendHandshakeAsync(context.RequestAborted);

        connection.AttachBrowser(browser);

        logger.LogInformation(
            "browser attached to {DeviceId} ({Count} total)", deviceId, connection.BrowserCount);

        // One receive covers a whole chunk in the ordinary case, and the accumulator
        // is what handles a frame the socket chose to deliver in pieces.
        var limit = SessionChunk.MaxPayload + SessionChunk.HeaderSize;
        var buffer = ArrayPool<byte>.Shared.Rent(limit);
        var message = new ArrayBufferWriter<byte>(limit);

        // An ArrayBufferWriter GROWS. Without this a browser decides how much of
        // the relay's memory to take, and the rented buffer above is only a hint.
        //
        // This is also the ONLY place an over-window chunk can be refused, which
        // cost a redeploy to learn: RelayFromBrowserAsync checks the size too, but
        // an oversized chunk never reaches it, because it is discarded here first.
        // So the refusal belongs here, with the session id read off the first
        // fragment before the bytes go -- otherwise the browser is told nothing and
        // sits until the DEVICE's receive timeout expires, blaming the device for
        // something this relay decided.
        var discarding = false;
        ushort discardedSession = 0;
        var haveDiscardedHeader = false;

        try
        {
            while (!context.RequestAborted.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (discarding || message.WrittenCount + result.Count > limit)
                {
                    if (!discarding)
                    {
                        logger.LogWarning(
                            "browser on {DeviceId} sent a chunk over this relay's "
                            + "{Limit}-byte window - refusing that channel", deviceId,
                            SessionChunk.MaxPayload);

                        // The header is at the head of the message, which is either
                        // already accumulated or at the start of this fragment.
                        if (message.WrittenCount >= SessionChunk.HeaderSize)
                        {
                            (discardedSession, _) =
                                SessionChunk.ReadHeader(message.WrittenSpan);
                            haveDiscardedHeader = true;
                        }
                        else if (message.WrittenCount == 0
                                 && result.Count >= SessionChunk.HeaderSize)
                        {
                            (discardedSession, _) =
                                SessionChunk.ReadHeader(buffer.AsSpan(0, result.Count));
                            haveDiscardedHeader = true;
                        }
                    }
                    discarding = true;
                    message.ResetWrittenCount();
                    // Read to the end of the message anyway, or its tail is read as
                    // the next chunk's header.
                    if (!result.EndOfMessage)
                        continue;
                    discarding = false;

                    if (haveDiscardedHeader)
                    {
                        haveDiscardedHeader = false;

                        // Tearing the session down matters as much as saying why.
                        // Refusing the chunk and stopping there leaves the device
                        // waiting for a body that will never come, holding the gate
                        // until its own receive timeout -- so the next command gets
                        // "busy" for ten seconds over something this relay already
                        // decided. Measured on v0.8.5: two refusals, then a pass.
                        //
                        // Routed through RelayFromBrowserAsync rather than undone by
                        // hand, because a RESET from a browser is exactly what this
                        // is, and that path already forwards it to the device, drops
                        // the mappings and releases the gate. An unmapped session
                        // falls out of it as residue, which is also right.
                        await connection.RelayFromBrowserAsync(
                            browser,
                            SessionChunk.Frame(discardedSession, SessionChunk.FlagReset,
                                Encoding.UTF8.GetBytes("chunk over the relay's window")),
                            context.RequestAborted);

                        await browser.SendAsync(
                            SessionChunk.Frame(discardedSession, SessionChunk.FlagReset,
                                Encoding.UTF8.GetBytes("chunk over the relay's window")),
                            context.RequestAborted);
                    }
                    continue;
                }

                message.Write(buffer.AsSpan(0, result.Count));
                if (!result.EndOfMessage)
                    continue;

                var span = message.WrittenSpan;
                if (span.Length >= SessionChunk.HeaderSize
                    && (span[2] & SessionChunk.FlagControl) != 0)
                {
                    // Connection-level, so it never reaches the device: the two
                    // pipes handshake independently.
                    if (!browser.Settle(span[SessionChunk.HeaderSize..], out var version))
                    {
                        if (version != SessionChunk.ProtocolVersion)
                        {
                            logger.LogWarning(
                                "browser on {DeviceId} speaks protocol {Version} - closing",
                                deviceId, version);
                            break;
                        }
                        // A nonce collision: redraw and say so again.
                        await browser.SendHandshakeAsync(context.RequestAborted);
                    }
                    message.ResetWrittenCount();
                    continue;
                }

                if (connection.Protocol == SessionChunk.Wire.Channels && !browser.Ready)
                {
                    logger.LogWarning(
                        "browser on {DeviceId} sent channel traffic before READY - dropped",
                        deviceId);
                    message.ResetWrittenCount();
                    continue;
                }

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
