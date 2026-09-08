namespace StruxRelay.Models;

/// <summary>The relay itself. Connected devices are not in here.</summary>
internal sealed record Health(string Status, string Version);

internal sealed record Session(Health Health);

/// <summary>
/// Everything the pairing page draws. One call rather than three, because the
/// three lists are read together and a device moving between them should never be
/// visible half-done.
/// </summary>
internal sealed record PairingState(
    IReadOnlyList<PendingDeviceView> Pending,
    IReadOnlyList<ApprovedDeviceView> Approved,
    IReadOnlyList<RelayEventView> Events);

/// <summary>
/// A refused attempt. The token IS shown here, because approving is keyed on the
/// pair and the operator is deciding about this exact token.
/// </summary>
internal sealed record PendingDeviceView(
    string DeviceId,
    string Token,
    string Name,
    string Project,
    string Firmware,
    DateTime FirstSeen,
    DateTime LastSeen,
    int Attempts);

/// <summary>
/// An approved device. Note what is missing: the token. The Python relay's
/// /api/pairing shipped it to the browser with the rest of the row, and nothing on
/// the page ever needed it — an approval is addressed by id from here on.
/// </summary>
internal sealed record ApprovedDeviceView(
    string DeviceId,
    string Name,
    string Project,
    string Firmware,
    DateTime ApprovedAt,
    DateTime? LastSeen);

internal sealed record RelayEventView(
    long Id,
    DateTime At,
    string Kind,
    string? DeviceId,
    string Detail);

/// <summary>May this device have the pipe, and if not, what to tell it.</summary>
internal sealed record ConnectDecision(bool Allowed, string Reason)
{
    public static readonly ConnectDecision Allow = new(true, "");
}

internal sealed record ApproveResult(bool Ok, string? Error = null);

internal sealed record ForgetResult(bool Ok, bool WasApproved);
