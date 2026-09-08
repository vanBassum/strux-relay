using System.Net.WebSockets;

namespace StruxRelay.Devices;

/// <summary>
/// One browser's socket onto a device pipe. Deliberately thin: every session id
/// belongs to <see cref="DeviceConnection"/>, which owns the id space a browser's
/// ids are rewritten into, so all this adds is the send lock.
///
/// That lock is not optional. A fanned-out log line and a command reply reach the
/// same browser from two different tasks — the device's read loop is one task per
/// pipe, but the fan-out walks every browser — and a WebSocket permits exactly one
/// send at a time.
/// </summary>
internal sealed class BrowserConnection(WebSocket socket)
{
    private readonly SemaphoreSlim sendLock = new(1, 1);

    public WebSocket Socket { get; } = socket;

    public bool Online => Socket.State == WebSocketState.Open;

    /// <summary>
    /// Sends one already-framed chunk, reporting failure rather than throwing.
    /// A browser closing its tab mid-broadcast is the ordinary case, and the
    /// fan-out's answer is to drop that browser and carry on with the others —
    /// which an exception per dead socket would turn into control flow.
    /// </summary>
    public async Task<bool> SendAsync(
        ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
    {
        try
        {
            await sendLock.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            if (!Online)
                return false;

            await Socket.SendAsync(
                chunk, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
            return true;
        }
        catch (Exception exception) when (
            exception is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
        finally
        {
            sendLock.Release();
        }
    }

    /// <summary>
    /// Ends this browser's socket from another task — what a device pipe closing
    /// does to the browsers hanging off it. Abort and not CloseAsync, for the same
    /// reason the device pipe aborts: the browser's own read loop is sitting in
    /// ReceiveAsync, and closing a socket with a receive outstanding is invalid.
    /// </summary>
    public void Abort()
    {
        try
        {
            Socket.Abort();
        }
        catch (ObjectDisposedException)
        {
            // Its own handler got there first. Nothing left to end.
        }
    }
}
