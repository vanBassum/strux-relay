using StruxRelay.Data;
using StruxRelay.Models;

namespace StruxRelay.Devices;

/// <summary>
/// One list of devices, assembled from the two places that know about them: the
/// pairing store, which says whether a device MAY connect, and the registry, which
/// says whether it IS connected.
///
/// Rows are keyed on the (id, token) pair rather than the id alone, and that is
/// what the pending case needs: two tokens claiming one id is what somebody
/// guessing looks like, and collapsing them into one row would both hide it and
/// make "approve" ambiguous about which token it pinned. An approved device
/// carries no token, so it is one row.
/// </summary>
internal sealed class DeviceDirectory(PairingStore pairing, DeviceRegistry registry)
{
    public async Task<IReadOnlyList<DeviceView>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var state = await pairing.GetStateAsync(cancellationToken: cancellationToken);
        var devices = new List<DeviceView>(state.Approved.Count + state.Pending.Count);

        foreach (var device in state.Approved)
        {
            var live = registry.Find(device.DeviceId);
            // The live pipe's copy wins while there is one: a device that renamed
            // itself and reconnected has already said so, and the stored row is
            // written from the same hello a moment later.
            var hello = live?.Hello ?? DeviceHello.FromJson(device.Hello);
            devices.Add(new DeviceView(
                device.DeviceId,
                device.Name,
                device.Project,
                device.Firmware,
                live is not null && live.Online ? Connection.Online : Connection.Offline,
                Approval.Approved,
                // The last time the device actually said something, which for a
                // live pipe the database does not know: its last-seen column is
                // written at connect and never again. NOT DateTime.UtcNow, which
                // is what this used to be — that is a fact about when the list
                // was built, dressed up as a fact about the device.
                live is not null ? live.LastMessageAt : device.LastSeen,
                // What the device SAYS its address is, falling back to what the socket
                // observed. The observed one is this relay's own docker-network peer —
                // Traefik — so it was 172.18.0.x for every device in the list, which
                // is an address nobody can reach anything at.
                NullIfEmpty(hello.Ip) ?? live?.Address,
                live?.ConnectedAt,
                live?.LastMessageAt,
                device.ApprovedAt,
                Token: null,
                Attempts: null,
                Commit: NullIfEmpty(live?.Commit ?? device.Commit),
                Details: hello.Rest,
                Description: NullIfEmpty(hello.Description),
                McpExposed: device.McpExposed));
        }

        foreach (var device in state.Pending)
        {
            devices.Add(new DeviceView(
                device.DeviceId,
                device.Name,
                device.Project,
                device.Firmware,
                // A pending device is never connected: it is refused before the
                // upgrade, so there is no pipe to be online on.
                Connection.Offline,
                Approval.Pending,
                device.LastSeen,
                Address: null,
                ConnectedAt: null,
                LastMessageAt: null,
                ApprovedAt: null,
                device.Token,
                device.Attempts,
                // A pending device has said nothing the relay is willing to repeat: it
                // is refused before the upgrade, so there is no socket for a hello.
                Commit: null,
                Details: null,
                Description: null,
                // Nothing to expose: a device that may not connect cannot be reached
                // by anything, MCP included.
                McpExposed: false));
        }

        // Pending first — they are the rows that want a decision — then by name,
        // which is what the list shows first.
        return
        [
            .. devices
                .OrderBy(device => device.Approval == Approval.Pending ? 0 : 1)
                .ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(device => device.DeviceId, StringComparer.Ordinal)
        ];
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;
}
