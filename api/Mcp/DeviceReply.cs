using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace StruxRelay.Mcp;

/// <summary>
/// Turns one device reply into MCP content blocks, knowing nothing about any
/// device, product or command.
///
/// A Strux reply either is JSON and nothing else, or it is a JSON header line, a
/// newline, and then a body of raw bytes. The header declares what the body is in
/// the one field that matters here — <c>contentType</c>, an ordinary IANA media
/// type (see main/lib/protocol/ReplyBody.h in the firmware). Its PRESENCE is what
/// says there is a body at all.
///
/// That is the entire basis on which this file decides anything. It never looks at
/// the command that was called: <c>fs read</c> of an SVG and <c>render svg</c> of a
/// PNG arrive here as "some bytes that say they are image/svg+xml" and "some bytes
/// that say they are image/png", and a firmware command invented tomorrow that
/// declares a media type is handled correctly by this code as written today. A
/// reply that declares nothing is text, which is what every ordinary command
/// answers and why old firmware keeps working unchanged.
/// </summary>
internal static class DeviceReply
{
    /// <summary>
    /// How much TEXT travels back through a tool call. The pipe allows a
    /// megabyte and some commands will use it — <c>log list</c> is the honest
    /// example — and a megabyte of JSON is a fine thing to hand a browser and a
    /// poor thing to hand a model. A long text reply is cut and SAYS it was cut.
    ///
    /// Binary is never cut. Half a PNG is not a smaller PNG, it is a broken one,
    /// and a caller cannot "ask for less" of an image the way it can ask for a
    /// narrower log. What bounds binary is the pipe's own 1 MB ceiling, which
    /// refuses the reply outright rather than trimming it.
    /// </summary>
    private const int MaxTextChars = 128 * 1024;

    /// <summary>
    /// The reply as content blocks: the header as text, then the body as whatever
    /// its media type makes it.
    /// </summary>
    public static IList<ContentBlock> ToContent(byte[] reply)
    {
        var (header, contentType, body) = Split(reply);

        if (contentType is null)
            // No body was declared, so the whole reply is the answer. Byte-for-byte
            // what this tool has always returned, which is what keeps every existing
            // caller working.
            return [Text(Encoding.UTF8.GetString(reply))];

        var blocks = new List<ContentBlock> { Text(header) };
        blocks.Add(BodyBlock(contentType, body));
        return blocks;
    }

    /// <summary>
    /// Header line, declared media type, and body. The media type is null when the
    /// reply declared none, and then the body is meaningless and ignored.
    ///
    /// Deliberately forgiving: a reply that is not JSON, or whose first line is not
    /// an object, is simply one without a body. What a device said is a fact to
    /// report, not a contract to enforce.
    /// </summary>
    private static (string Header, string? ContentType, ReadOnlyMemory<byte> Body) Split(
        byte[] reply)
    {
        var newline = Array.IndexOf(reply, (byte)'\n');
        if (newline < 0) return (string.Empty, null, default);

        var headerBytes = reply.AsMemory(0, newline);
        string? contentType;
        try
        {
            using var document = JsonDocument.Parse(headerBytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return (string.Empty, null, default);
            contentType =
                document.RootElement.TryGetProperty("contentType", out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return (string.Empty, null, default);
        }

        if (string.IsNullOrWhiteSpace(contentType)) return (string.Empty, null, default);

        return (
            Encoding.UTF8.GetString(headerBytes.Span),
            contentType,
            reply.AsMemory(newline + 1));
    }

    /// <summary>
    /// One body, as the kind of block its media type deserves.
    ///
    /// Three cases, in this order, and the order is the point:
    ///
    ///   1. TEXTUAL types become text. An SVG is <c>image/svg+xml</c> and is an
    ///      image by name only — it is a document a model can read and edit, and
    ///      handing it over as base64, or as an image block a client would fail to
    ///      decode, would make the obvious workflow (read a label, change it,
    ///      write it back) impossible. So this test runs BEFORE the image test.
    ///   2. Real IMAGE types become an image block, which is what puts a rendered
    ///      label in front of a multimodal client as a picture.
    ///   3. Everything else is base64 with a line saying so, because bytes that
    ///      cannot be shown must still be transportable.
    /// </summary>
    private static ContentBlock BodyBlock(string contentType, ReadOnlyMemory<byte> body)
    {
        if (IsTextual(contentType))
            return Text(Truncate(Encoding.UTF8.GetString(body.Span), contentType));

        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            // FromBytes rather than setting Data: the property is base64 as UTF-8
            // BYTES, not a string, and the SDK does that encoding better than a
            // round trip through one would.
            return ImageContentBlock.FromBytes(body, contentType);

        return Text(
            $"[{body.Length} bytes of {contentType}, base64]\n"
            + Convert.ToBase64String(body.Span));
    }

    /// <summary>
    /// Is this media type a document rather than a blob?
    ///
    /// Structural, not a list of blessed names: anything under <c>text/</c>, and
    /// any structured suffix that means text (<c>+xml</c>, <c>+json</c>), so a
    /// media type nobody here has heard of lands on the right side by its shape.
    /// That is what makes <c>image/svg+xml</c> text without this file knowing what
    /// an SVG is.
    /// </summary>
    private static bool IsTextual(string contentType)
    {
        var type = contentType.Split(';')[0].Trim();

        if (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) return true;
        if (type.EndsWith("+xml", StringComparison.OrdinalIgnoreCase)) return true;
        if (type.EndsWith("+json", StringComparison.OrdinalIgnoreCase)) return true;

        return type.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || type.Equals("application/xml", StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string text, string contentType) =>
        text.Length <= MaxTextChars
            ? text
            : text[..MaxTextChars]
              + $"\n...truncated by the relay at {MaxTextChars} characters "
              + $"({text.Length} in total, {contentType}) - ask for less at a time";

    public static ContentBlock Text(string text) => new TextContentBlock { Text = text };
}
