using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using StruxRelay.Data;
using StruxRelay.Devices;
using StruxRelay.Models;

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
    DeviceRegistry registry) : Hub
{
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
}
