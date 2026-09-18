using System.Text.Json;

namespace StruxRelay.Models;

/// <summary>
/// What a device says about itself on connect: a flat map of string keys to string
/// values, all optional, with no fixed schema.
///
/// This replaces the query string it used to ride in (<c>?id=…&amp;fw=…&amp;name=…</c>).
/// Every new fact was a new parameter there, which cost percent-encoding, a slice of a
/// fixed buffer on the device and a change on both sides — and it put display data in
/// the one part of a connection that is logged, proxied and cached. Only <c>id</c> is
/// left in the URL, because it is the one field the token proves and the one anything
/// is keyed on.
///
/// The relay STORES what it gets, SHOWS what it understands and IGNORES the rest, and
/// that is what makes it forward compatible in both directions: a newer device
/// reporting a key this relay never heard of costs nothing, and an older device
/// omitting one is a blank cell rather than a failed connect.
/// </summary>
internal sealed record DeviceHello(IReadOnlyDictionary<string, string> Values)
{
    public static readonly DeviceHello Empty =
        new(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Firmware version — the git tag, e.g. "0.1.0".</summary>
    public string? Firmware => Value("fw");

    /// <summary>
    /// The git commit the firmware was built from. The tag alone does not identify a
    /// build: two boards can both say 0.1.0 and be different code, which is exactly
    /// the confusion a device list exists to prevent.
    /// </summary>
    public string? Commit => Value("commit");

    public string? Name => Value("name");

    public string? Project => Value("project");

    /// <summary>
    /// The address the device holds on its OWN network, which is the only party that
    /// knows it. What the socket reports is wherever the connection emerged — a NAT,
    /// and behind this relay's reverse proxy the proxy's peer address on the container
    /// network, which is the same 172.18.0.x for every device on the list. So a
    /// reported address beats an observed one, and the observed one stays as the
    /// fallback for firmware that does not send this yet.
    /// </summary>
    public string? Ip => Value("ip");

    /// <summary>
    /// One line saying what this device IS, written by the firmware that is running
    /// on it. Short by construction — it shares the hello's one chunk with everything
    /// else the device says — so it answers "which board is this" and nothing more.
    /// The long form (how to drive it) is not here: it is served by the device's
    /// <c>system describe</c> command, on demand.
    /// </summary>
    public string? Description => Value("desc");

    private string? Value(string key) =>
        Values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    /// <summary>
    /// Everything this relay does not have a column for, which is most of what a
    /// device may choose to say. Kept so the answer to "what did it tell us" is
    /// never "whatever this build happened to understand".
    /// </summary>
    public IReadOnlyDictionary<string, string> Rest =>
        Values
            .Where(pair => !Understood.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static readonly HashSet<string> Understood =
        new(StringComparer.Ordinal) { "fw", "commit", "name", "project", "ip", "desc" };

    /// <summary>
    /// Parses one hello payload. Returns null for anything that is not a flat JSON
    /// object, which is the only shape this is — a nested value would have no cell to
    /// go in and no meaning to guess at.
    ///
    /// Numbers and booleans are accepted and stringified, because a device writing
    /// <c>"heap": 214000</c> means the obvious thing and refusing the whole hello over
    /// it would cost a device its name. Null is dropped rather than stored as "".
    ///
    /// <c>type</c> is dropped: the SESSION id is what names this chunk, and the device
    /// sends the field so a packet capture is self-describing, not so the relay has
    /// somewhere to put it.
    /// </summary>
    public static DeviceHello? Parse(ReadOnlySpan<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("type")) continue;

                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False =>
                        property.Value.GetRawText(),
                    _ => null,
                };

                if (value is not null) values[property.Name] = value;
            }

            return new DeviceHello(values);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// How the map is stored. JSON rather than a column per key, because the whole
    /// point is that the key set is the DEVICE's business: a column would mean a
    /// migration every time firmware learns to report one more thing.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(Values);

    public static DeviceHello FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return Empty;
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return values is null ? Empty : new DeviceHello(values);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }
}
