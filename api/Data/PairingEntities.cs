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

    /// <summary>The git commit the firmware was built from, when it reports one.</summary>
    public string Commit { get; set; } = "";

    /// <summary>
    /// The device's last hello, whole, as JSON. The columns above are the keys this
    /// relay understands well enough to sort and search on; this is everything it was
    /// told, so learning to display one more field is a frontend change rather than a
    /// migration. See <see cref="Models.DeviceHello"/>.
    /// </summary>
    public string Hello { get; set; } = "";

    public DateTime ApprovedAt { get; set; }

    public DateTime? LastSeen { get; set; }

    /// <summary>
    /// Whether this device is reachable through the relay's MCP surface.
    ///
    /// The relay is the trust boundary, so this is RELAY state and not something a
    /// device says about itself: a board cannot volunteer itself to an agent by
    /// reporting a flag. Off by default, including for devices approved before this
    /// column existed — approving a device lets a person drive it, which is not the
    /// same decision as letting a model drive it.
    ///
    /// Deliberately one boolean. Read/write/destructive tiers, per-command rules and
    /// a policy engine were all considered and left out: the relay does not know what
    /// any command means, so any tier it invented would be a guess about somebody
    /// else's firmware.
    /// </summary>
    public bool McpExposed { get; set; }
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

    /// <summary>
    /// What the LEGACY connect URL carried, and empty for anything newer: a device is
    /// refused before the upgrade, so a pending row is written before there is a
    /// socket for a hello to arrive on.
    ///
    /// That is not a gap to close. These are unauthenticated strings from a device
    /// nobody has vouched for yet, displayed beside an Approve button — the one place
    /// on this dashboard where a chosen name could mislead the person deciding. The
    /// id, the address and the attempt count are what the relay can actually stand
    /// behind, and they are what the decision is made on.
    /// </summary>
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
