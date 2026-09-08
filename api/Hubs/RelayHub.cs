using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using StruxRelay.Data;
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
internal sealed class RelayHub(PairingStore pairing) : Hub
{
    private static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Reports the relay itself. Liveness only — a connected device is not a health
    /// condition, which is also why /healthz says nothing about them.
    /// </summary>
    public Session GetSession() => new(new Health("ok", Version));

    /// <summary>
    /// The pairing lists: refused attempts waiting for a decision, the approvals
    /// that stand, and the recent state changes. This is the state a dashboard
    /// catches up on when it opens; after that the store pushes "PairingChanged"
    /// to everyone, so nothing here is polled.
    /// </summary>
    public Task<PairingState> GetPairing() =>
        pairing.GetStateAsync(cancellationToken: Context.ConnectionAborted);

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
    /// Revoke a device: its approval, its pending rows, and — once the pipe lands —
    /// its live socket and anything cached for it. The token is only checked when a
    /// connection is made, so a revoked device that is already connected stays
    /// connected until it is dropped.
    /// </summary>
    public Task<ForgetResult> Forget(string deviceId) =>
        pairing.ForgetAsync(deviceId, Context.ConnectionAborted);

    // Still to come with the pipe, since each one reads live connection state:
    // GetDevices (the connected list), FlushCache, and the telemetry counters.
}
