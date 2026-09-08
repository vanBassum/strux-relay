using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using StruxRelay.Hubs;

namespace StruxRelay.Devices;

/// <summary>
/// The pipes this process is holding. In memory by necessity — a device's socket
/// lives in the process that accepted it, which is also why a second relay
/// instance could not serve this one's devices without routing by device id.
/// </summary>
internal sealed class DeviceRegistry(
    IHubContext<RelayHub> hub,
    ILogger<DeviceRegistry> logger)
{
    private readonly ConcurrentDictionary<string, DeviceConnection> connections = new();

    public DeviceConnection? Find(string deviceId) =>
        connections.TryGetValue(deviceId, out var connection) ? connection : null;

    public IReadOnlyCollection<DeviceConnection> Connected() => [.. connections.Values];

    /// <summary>
    /// Takes over the slot for this device id, closing whatever held it. A device
    /// that reconnects before the relay noticed the old socket was dead is the
    /// ordinary case, not an error.
    /// </summary>
    public async Task AddAsync(DeviceConnection connection)
    {
        if (connections.TryGetValue(connection.DeviceId, out var previous))
        {
            logger.LogInformation(
                "device {DeviceId} reconnected on pipe #{Pipe}, dropping pipe #{Previous}",
                connection.DeviceId, connection.Pipe, previous.Pipe);
            await previous.CloseAsync();
        }

        connections[connection.DeviceId] = connection;
        await AnnounceAsync();
    }

    /// <summary>
    /// Gives up the slot, but only if this pipe still holds it. Whether it does is
    /// what decides what a teardown MEANS: the device going away, or a pipe that
    /// was already replaced finally closing. Saying the wrong one reads like a
    /// healthy device dropping every few seconds.
    /// </summary>
    public async Task<bool> RemoveAsync(DeviceConnection connection)
    {
        var current = connections.TryGetValue(connection.DeviceId, out var held)
            && ReferenceEquals(held, connection);

        if (current)
        {
            connections.TryRemove(connection.DeviceId, out _);
            await AnnounceAsync();
        }

        return current;
    }

    /// <summary>
    /// Drops a device's pipe on revocation. The token is only checked when a
    /// connection is made, so without this a device stays connected after being
    /// forgotten.
    /// </summary>
    public async Task DropAsync(string deviceId)
    {
        if (!connections.TryRemove(deviceId, out var connection))
            return;

        await connection.CloseAsync();
        await AnnounceAsync();
    }

    private Task AnnounceAsync() => hub.Clients.All.SendAsync("DevicesChanged");
}
