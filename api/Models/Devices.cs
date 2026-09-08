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
    int? UptimeSeconds,
    DateTime? ApprovedAt,
    string? Token,
    int? Attempts);
