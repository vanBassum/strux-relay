namespace StruxRelay.Telemetry;

/// <summary>
/// One measurement, as it arrived and as the diagnostics page needs to show it.
///
/// <see cref="Line"/> is the authority and is forwarded byte for byte. Everything
/// else is a reading of it, for display only — the relay does not need to
/// understand a point to move it, and the moment it started rewriting one it
/// would own a format the firmware defines.
/// </summary>
internal sealed record TelemetryPoint(
    long Sequence,
    DateTime ReceivedAt,
    string DeviceId,
    string DeviceName,
    string Line,
    string Measurement,
    // The field set, verbatim: temperature=21.5,unit="C"
    string Fields,
    // Tags other than `device`, which has a column of its own. Empty when none.
    string Tags);

/// <summary>
/// Just enough line-protocol reading to fill a diagnostics table: the
/// measurement, the tags and the field set.
///
/// Deliberately not a full parser and never used on the forwarding path. It has
/// to respect escaping anyway, because getting that wrong would not fail — it
/// would quietly show the wrong measurement for any point with a comma or a space
/// in a tag, which is exactly the point somebody would be staring at this page to
/// understand.
/// </summary>
internal static class LineProtocol
{
    /// <summary>
    /// Splits <c>measurement,tags fields [timestamp]</c>. Returns false for a line
    /// that is not shaped like a point at all, which the caller counts as invalid.
    /// </summary>
    public static bool TryRead(
        string line,
        out string measurement,
        out string tags,
        out string fields)
    {
        measurement = "";
        tags = "";
        fields = "";

        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return false;

        // The key section ends at the first space that is neither escaped nor
        // inside a quoted string. Tag values cannot be quoted, so quote tracking
        // matters only in the field section — but sharing one scanner keeps the
        // two from disagreeing.
        var keyEnd = IndexOfUnescapedSpace(trimmed, 0);
        if (keyEnd < 0)
            return false;

        var keys = trimmed[..keyEnd];
        var rest = trimmed[(keyEnd + 1)..].TrimStart();
        if (rest.Length == 0)
            return false;

        // A field value may contain spaces inside quotes, so the timestamp is
        // whatever follows the next unescaped space OUTSIDE a quoted string —
        // not simply the text after the last space.
        var fieldEnd = IndexOfUnescapedSpace(rest, 0);
        fields = fieldEnd < 0 ? rest : rest[..fieldEnd];

        var comma = IndexOfUnescapedComma(keys, 0);
        if (comma < 0)
        {
            measurement = Unescape(keys);
            return measurement.Length > 0;
        }

        measurement = Unescape(keys[..comma]);
        tags = keys[(comma + 1)..];
        return measurement.Length > 0;
    }

    /// <summary>
    /// Pulls the value of one tag out of a tag section, and drops it from what is
    /// left. Used for <c>device</c>, which the firmware puts on every point and
    /// which the table shows in its own column rather than among the tags.
    /// </summary>
    public static string TakeTag(ref string tags, string name)
    {
        var value = "";
        if (tags.Length == 0)
            return value;

        var kept = new List<string>();
        var start = 0;
        while (start <= tags.Length)
        {
            var comma = IndexOfUnescapedComma(tags, start);
            var pair = comma < 0 ? tags[start..] : tags[start..comma];

            var equals = IndexOfUnescaped(pair, '=', 0);
            if (equals > 0 && Unescape(pair[..equals]) == name)
                value = Unescape(pair[(equals + 1)..]);
            else if (pair.Length > 0)
                kept.Add(pair);

            if (comma < 0)
                break;
            start = comma + 1;
        }

        tags = string.Join(",", kept);
        return value;
    }

    private static int IndexOfUnescapedSpace(string text, int from)
    {
        var quoted = false;
        for (var i = from; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\')
            {
                i++;
                continue;
            }
            if (c == '"')
                quoted = !quoted;
            else if (c == ' ' && !quoted)
                return i;
        }
        return -1;
    }

    private static int IndexOfUnescapedComma(string text, int from) =>
        IndexOfUnescaped(text, ',', from);

    private static int IndexOfUnescaped(string text, char wanted, int from)
    {
        for (var i = from; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\')
            {
                i++;
                continue;
            }
            if (c == wanted)
                return i;
        }
        return -1;
    }

    private static string Unescape(string text)
    {
        if (!text.Contains('\\'))
            return text;

        var value = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
                i++;
            value.Append(text[i]);
        }
        return value.ToString();
    }
}
