namespace StruxRelay.Data;

/// <summary>
/// A device that may have the pipe, and the token it proved itself with.
///
/// The id is technical and public — it is derived from the board's MAC and it is
/// in every /devices/&lt;id&gt;/ URL. The token is the secret, and it is the
/// <em>device</em> that generates it: the relay only ever pins the value a device
/// presented, so there is no relay-to-device message that could set one.
/// </summary>
internal sealed class ApprovedDevice
{
    public string DeviceId { get; set; } = "";

    public string Token { get; set; } = "";

    /// <summary>Display only, so a rename costs a device nothing.</summary>
    public string Name { get; set; } = "";

    public string Project { get; set; } = "";

    public string Firmware { get; set; } = "";

    public DateTime ApprovedAt { get; set; }

    public DateTime? LastSeen { get; set; }
}

/// <summary>
/// A connect attempt that was refused, waiting for somebody to approve it.
///
/// Keyed on the PAIR and not the id: two different tokens claiming one id is what
/// somebody guessing looks like, and collapsing them into one row would hide
/// exactly that.
/// </summary>
internal sealed class PendingDevice
{
    public string DeviceId { get; set; } = "";

    public string Token { get; set; } = "";

    public string Name { get; set; } = "";

    public string Project { get; set; } = "";

    public string Firmware { get; set; } = "";

    public DateTime FirstSeen { get; set; }

    public DateTime LastSeen { get; set; }

    /// <summary>
    /// How often this pair has asked. A device retries until somebody approves it,
    /// so the repeat signal lives here — one row that counts — rather than as N
    /// event rows that each say the same thing.
    /// </summary>
    public int Attempts { get; set; } = 1;
}

/// <summary>
/// A state change worth showing an operator: a first sighting, an approval, a
/// removal. Grows with operator actions and new devices, never with traffic.
/// </summary>
internal sealed class RelayEvent
{
    public long Id { get; set; }

    public DateTime At { get; set; }

    public string Kind { get; set; } = "";

    public string? DeviceId { get; set; }

    public string Detail { get; set; } = "";
}
