using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StruxRelay.Hubs;
using StruxRelay.Models;

namespace StruxRelay.Data;

/// <summary>
/// The connect decision, and the lists an operator pairs from.
///
/// A device must be approved and must present the token it was approved with, or
/// the upgrade is refused. That refusal is not a dead end — it is the only way a
/// new device ever gets paired: it is refused once, shows up as pending, and the
/// reconnect after approval succeeds. Nothing is ever pushed to a device, because
/// it is already retrying every few seconds.
/// </summary>
internal sealed class PairingStore(
    IDbContextFactory<RelayDbContext> contexts,
    IHubContext<RelayHub> hub,
    ILogger<PairingStore> logger)
{
    /// <summary>
    /// An endpoint that INSERTs and that strangers can reach is a disk-filling
    /// machine, so the pending list is bounded. Past the cap a KNOWN pair still
    /// counts its attempts — the signal keeps rising — but no new row is made.
    /// </summary>
    private const int MaxPending = 50;

    private const int MaxEvents = 500;

    /// <summary>
    /// Connects are rare and the decision is read-modify-write across two tables,
    /// so they are serialised rather than raced. Two devices asking at the same
    /// instant would otherwise both find no pending row and both insert one.
    /// </summary>
    private readonly SemaphoreSlim gate = new(1, 1);

    // ── the connect decision ──────────────────────────────────────────────────

    public async Task<ConnectDecision> AuthenticateAsync(
        string deviceId,
        string token,
        string name,
        string project,
        string firmware,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var database = await contexts.CreateDbContextAsync(cancellationToken);
            var now = DateTime.UtcNow;

            var approved = await database.Approved
                .FirstOrDefaultAsync(device => device.DeviceId == deviceId, cancellationToken);

            if (approved is not null)
            {
                if (TokenMatches(approved.Token, token))
                {
                    approved.LastSeen = now;
                    approved.Name = name;
                    approved.Project = project;
                    approved.Firmware = firmware;
                    await database.SaveChangesAsync(cancellationToken);
                    return ConnectDecision.Allow;
                }

                // Approved id, wrong token. Either somebody is guessing, or this is
                // the same board after an NVS wipe — the MAC-derived id survives
                // that and the token does not. The two are indistinguishable from
                // here, so record and refuse: the operator decides which it was.
                await RefuseAsync(database, deviceId, token, name, project, firmware, now,
                    "token mismatch", cancellationToken);
                return new ConnectDecision(false, "token mismatch");
            }

            await RefuseAsync(database, deviceId, token, name, project, firmware, now,
                "not approved", cancellationToken);
            return new ConnectDecision(false, "device not approved");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Constant time rather than string equality: how long a mismatch takes is the
    /// one thing here that would tell an attacker something. An empty presented
    /// token is a mismatch outright, so a device that sends no header is refused
    /// without comparing anything.
    /// </summary>
    private static bool TokenMatches(string approved, string presented) =>
        presented.Length > 0
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(approved), Encoding.UTF8.GetBytes(presented));

    private async Task RefuseAsync(
        RelayDbContext database,
        string deviceId,
        string token,
        string name,
        string project,
        string firmware,
        DateTime now,
        string reason,
        CancellationToken cancellationToken)
    {
        var isNew = await RememberPendingAsync(
            database, deviceId, token, name, project, firmware, now, cancellationToken);

        // Only the first sighting of a pair is an event. Logging one per refusal
        // filled the dashboard with the same line forty times and pushed the
        // approval that mattered off the top.
        if (isNew)
        {
            await LogEventAsync(database, "refused", deviceId, reason, cancellationToken);
            await AnnounceAsync();
        }
    }

    private async Task<bool> RememberPendingAsync(
        RelayDbContext database,
        string deviceId,
        string token,
        string name,
        string project,
        string firmware,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var pending = await database.Pending.FirstOrDefaultAsync(
            device => device.DeviceId == deviceId && device.Token == token, cancellationToken);

        if (pending is not null)
        {
            pending.LastSeen = now;
            pending.Attempts += 1;
            pending.Name = name;
            pending.Project = project;
            pending.Firmware = firmware;
            await database.SaveChangesAsync(cancellationToken);
            return false;
        }

        if (await database.Pending.CountAsync(cancellationToken) >= MaxPending)
        {
            logger.LogWarning(
                "pending list is full ({MaxPending}) — not recording {DeviceId}",
                MaxPending, deviceId);
            return false;
        }

        database.Pending.Add(new PendingDevice
        {
            DeviceId = deviceId,
            Token = token,
            Name = name,
            Project = project,
            Firmware = firmware,
            FirstSeen = now,
            LastSeen = now,
        });
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ── what the dashboard drives ─────────────────────────────────────────────

    public async Task<PairingState> GetStateAsync(
        int events = 20, CancellationToken cancellationToken = default)
    {
        await using var database = await contexts.CreateDbContextAsync(cancellationToken);

        return new PairingState(
            await database.Pending
                .OrderBy(device => device.FirstSeen)
                .Select(device => new PendingDeviceView(
                    device.DeviceId, device.Token, device.Name, device.Project,
                    device.Firmware, device.FirstSeen, device.LastSeen, device.Attempts))
                .ToListAsync(cancellationToken),
            await database.Approved
                .OrderBy(device => device.DeviceId)
                .Select(device => new ApprovedDeviceView(
                    device.DeviceId, device.Name, device.Project, device.Firmware,
                    device.ApprovedAt, device.LastSeen))
                .ToListAsync(cancellationToken),
            await database.Events
                .OrderByDescending(entry => entry.At)
                .Take(events)
                .Select(entry => new RelayEventView(
                    entry.Id, entry.At, entry.Kind, entry.DeviceId, entry.Detail))
                .ToListAsync(cancellationToken));
    }

    /// <summary>
    /// Pin the token this pair presented. Replaces any existing approval for the
    /// id, which is what re-pairing a wiped board is.
    /// </summary>
    public async Task<ApproveResult> ApproveAsync(
        string deviceId, string token, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var database = await contexts.CreateDbContextAsync(cancellationToken);

            var pending = await database.Pending.FirstOrDefaultAsync(
                device => device.DeviceId == deviceId && device.Token == token,
                cancellationToken);
            if (pending is null)
                return new ApproveResult(false, "no such pending device");

            var approved = await database.Approved.FirstOrDefaultAsync(
                device => device.DeviceId == deviceId, cancellationToken);
            if (approved is null)
            {
                approved = new ApprovedDevice { DeviceId = deviceId };
                database.Approved.Add(approved);
            }

            approved.Token = token;
            approved.Name = pending.Name;
            approved.Project = pending.Project;
            approved.Firmware = pending.Firmware;
            approved.ApprovedAt = DateTime.UtcNow;

            await database.SaveChangesAsync(cancellationToken);

            // Every pending row for this id goes, not just the approved pair: the
            // others were guesses at an id that is now settled.
            await database.Pending
                .Where(device => device.DeviceId == deviceId)
                .ExecuteDeleteAsync(cancellationToken);

            await LogEventAsync(database, "approved", deviceId, "", cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        logger.LogInformation("approved device {DeviceId}", deviceId);
        await AnnounceAsync();
        return new ApproveResult(true);
    }

    public async Task<ForgetResult> ForgetAsync(
        string deviceId, CancellationToken cancellationToken = default)
    {
        int removed;
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var database = await contexts.CreateDbContextAsync(cancellationToken);

            removed = await database.Approved
                .Where(device => device.DeviceId == deviceId)
                .ExecuteDeleteAsync(cancellationToken);
            await database.Pending
                .Where(device => device.DeviceId == deviceId)
                .ExecuteDeleteAsync(cancellationToken);

            if (removed > 0)
                await LogEventAsync(database, "forgotten", deviceId, "", cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        // Only when something actually went. Forgetting an id nobody has heard of
        // succeeds — it is already true — but it is not a change to announce.
        if (removed > 0)
        {
            logger.LogInformation("forgot device {DeviceId}", deviceId);
            await AnnounceAsync();
        }

        return new ForgetResult(true, removed > 0);
    }

    // ── the event log ─────────────────────────────────────────────────────────

    /// <summary>Records a state change and trims the log.</summary>
    private static async Task LogEventAsync(
        RelayDbContext database,
        string kind,
        string? deviceId,
        string detail,
        CancellationToken cancellationToken)
    {
        database.Events.Add(new RelayEvent
        {
            At = DateTime.UtcNow,
            Kind = kind,
            DeviceId = deviceId,
            Detail = detail,
        });
        await database.SaveChangesAsync(cancellationToken);

        // Trimmed on insert. An unbounded table on a path a stranger can reach is
        // the same disk-filling machine MaxPending exists to prevent, reached
        // through another table.
        var newest = await database.Events
            .MaxAsync(entry => (long?)entry.Id, cancellationToken) ?? 0;
        await database.Events
            .Where(entry => entry.Id <= newest - MaxEvents)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Tell every open dashboard that the lists moved. This is what the Python
    /// relay's polling of /api/pairing was standing in for.
    /// </summary>
    private Task AnnounceAsync() => hub.Clients.All.SendAsync("PairingChanged");
}
