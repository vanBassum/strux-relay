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
    string Commit,
    /// <summary>The device's last hello as stored JSON; see <see cref="DeviceHello"/>.</summary>
    string Hello,
    DateTime ApprovedAt,
    DateTime? LastSeen,
    /// <summary>Whether this device is reachable through the relay's MCP surface.</summary>
    bool McpExposed = false);

internal sealed record RelayEventView(
    long Id,
    DateTime At,
    string Kind,
    string? DeviceId,
    string Detail);

/// <summary>
/// What an older firmware put in its connect URL. A TRANSITION type, and named so it
/// reads as one at every call site: it exists so a device in the field that has not
/// been reflashed still fills in a device list, and it goes when they have been.
/// </summary>
internal sealed record LegacyIdentity(string Name, string Project, string Firmware);

/// <summary>May this device have the pipe, and if not, what to tell it.</summary>
internal sealed record ConnectDecision(bool Allowed, string Reason)
{
    public static readonly ConnectDecision Allow = new(true, "");
}

internal sealed record ApproveResult(bool Ok, string? Error = null);

/// <summary>One MCP credential, as the dashboard sees it: never the token itself.</summary>
internal sealed record McpTokenView(
    string Id,
    string Name,
    /// <summary>The token's first few characters — enough to match a row against a
    /// config file, not enough to be a credential.</summary>
    string Hint,
    DateTime CreatedAt,
    DateTime? LastUsedAt,
    DateTime? RevokedAt);

/// <summary>
/// Everything the MCP page draws. <see cref="Enabled"/> is what the endpoint will
/// actually DO rather than what is configured: with no credential of any kind it
/// refuses every request, and the page says so instead of leaving somebody to work it
/// out from a 503.
/// </summary>
internal sealed record McpView(
    bool Enabled,
    bool DeploymentTokenConfigured,
    IReadOnlyList<McpTokenView> Tokens);

/// <summary>
/// A freshly minted token, and the one time its <see cref="Token"/> is ever readable:
/// the relay keeps only a hash, so there is nothing to show again later.
/// </summary>
internal sealed record McpTokenCreated(
    bool Ok,
    string? Token,
    McpTokenView? Created,
    string? Error = null);

internal sealed record McpTokenResult(bool Ok, string? Error = null);

/// <summary>The result of flipping a device's MCP exposure.</summary>
internal sealed record McpExposureResult(bool Ok, bool Exposed, string? Error = null);

internal sealed record ForgetResult(bool Ok, bool WasApproved);
