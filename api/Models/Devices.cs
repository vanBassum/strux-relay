namespace StruxRelay.Models;

/// <summary>What the device said about one of its own files.</summary>
internal sealed record WebFileHeader(
    int Status,
    string? ContentType,
    string? ContentEncoding);

/// <summary>
/// A file read off a device. The body is passed through untouched — the device
/// stores its frontend gzipped and says so in the header, so the encoding travels
/// with the bytes rather than being undone and redone here.
/// </summary>
internal sealed record WebFile(WebFileHeader Header, byte[] Body);

/// <summary>Is the relay talking to this device right now.</summary>
internal enum Connection
{
    Offline,
    Online,
}

/// <summary>May it connect at all.</summary>
internal enum Approval
{
    /// <summary>Refused, and waiting for somebody to decide.</summary>
    Pending,

    Approved,
}

/// <summary>
/// One device, from every angle the dashboard cares about: whether it may connect,
/// whether it is connected, and what it last said about itself.
///
/// This is deliberately ONE row per device rather than separate pending and
/// approved lists. A device moves between those states and an operator thinks
/// about "that board", not about which table it currently lives in.
///
/// The token is carried only while the device is pending, because approving is
/// keyed on the (id, token) pair and the operator is deciding about that exact
/// token. An approved device's token is never sent to a browser.
/// </summary>
internal sealed record DeviceView(
    string DeviceId,
    string Name,
    string Project,
    string Firmware,
    Connection Connection,
    Approval Approval,
    DateTime? LastSeen,
    string? Address,
    DateTime? ConnectedAt,
    DateTime? LastMessageAt,
    DateTime? ApprovedAt,
    string? Token,
    int? Attempts,
    /// <summary>
    /// The git commit the firmware was built from, when it reports one. A tag does
    /// not identify a build — two boards can both say 0.1.0 and be different code.
    /// </summary>
    string? Commit = null,
    /// <summary>
    /// Everything else the device said about itself. Open by design: the relay stores
    /// what it gets and the dashboard shows what it understands, so a firmware that
    /// learns to report one more fact needs no change on this side at all.
    /// </summary>
    IReadOnlyDictionary<string, string>? Details = null,
    /// <summary>
    /// The one-line description the firmware reports in its hello, when it reports
    /// one. What the device IS, in its own words rather than in a name somebody typed.
    /// </summary>
    string? Description = null,
    /// <summary>
    /// Whether this device is reachable through the relay's MCP surface. Relay state,
    /// not something the device said — see <see cref="Data.ApprovedDevice.McpExposed"/>.
    /// Always false for a pending device: there is nothing to expose until it is let in.
    /// </summary>
    bool McpExposed = false);
