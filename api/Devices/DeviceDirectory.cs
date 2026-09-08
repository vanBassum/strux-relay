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
            devices.Add(new DeviceView(
                device.DeviceId,
                device.Name,
                device.Project,
                device.Firmware,
                live is not null && live.Online ? Connection.Online : Connection.Offline,
                Approval.Approved,
                // The live pipe is more recent than the row: last-seen is written
                // at connect, so a device that has been up for an hour would
                // otherwise read as last seen an hour ago.
                live is not null ? DateTime.UtcNow : device.LastSeen,
                live?.Address,
                live is not null
                    ? (int)(DateTime.UtcNow - live.ConnectedAt).TotalSeconds
                    : null,
                device.ApprovedAt,
                Token: null,
                Attempts: null));
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
                UptimeSeconds: null,
                ApprovedAt: null,
                device.Token,
                device.Attempts));
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
}
