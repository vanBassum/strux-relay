using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using StruxRelay.Data;
using StruxRelay.Devices;

namespace StruxRelay.Mcp;

/// <summary>
/// Everything the MCP tools do, minus the attributes. Three operations — list,
/// describe, execute — over the pipes the relay already holds.
///
/// It contains NO knowledge of any device. There is no table of commands here, no
/// notion of what a command means, and nothing keyed on a product: a device is
/// asked what it can do (<c>help describe</c>), asked what it is
/// (<c>system describe</c>), and then told to run whatever the caller named. A
/// firmware that grows a command, or a product this relay has never heard of,
/// becomes reachable through MCP with nothing changed on this side — which is the
/// whole design and the one property worth protecting when editing this file.
///
/// The gate is the relay's, because the relay is the trust boundary: every call
/// starts at <see cref="ConnectedAsync"/>, which requires the device to be approved,
/// marked exposed by an operator, and currently connected. A device failing any of
/// those is simply not there as far as MCP is concerned.
/// </summary>
internal sealed class DeviceMcp(
    PairingStore pairing,
    DeviceRegistry registry,
    ILogger<DeviceMcp> logger)
{
    /// <summary>
    /// How much of a device's reply travels back through a tool call.
    ///
    /// The pipe allows a megabyte (see DeviceConnection.MaxCommandReply) and some
    /// commands will happily use it — <c>log list</c> is the honest example. A
    /// megabyte of JSON is a fine thing to hand a browser and a poor thing to hand a
    /// model, so a long reply is cut and SAYS it was cut. It is not silently dropped
    /// and it is not summarised: the caller gets the beginning and a marker, and can
    /// go back with a narrower command.
    /// </summary>
    private const int MaxReplyChars = 128 * 1024;

    /// <summary>
    /// The devices an MCP client may see: approved, exposed by an operator, and
    /// connected right now. Anything else is absent rather than listed-and-refused —
    /// a model cannot act on a device it cannot reach, and a list of things it may not
    /// touch is an invitation to try.
    /// </summary>
    public async Task<string> ListAsync(CancellationToken cancellationToken)
    {
        var exposed = (await pairing.McpExposedIdsAsync(cancellationToken)).ToHashSet(
            StringComparer.Ordinal);

        var devices = new JsonArray();
        foreach (var connection in registry.Connected())
        {
            if (!exposed.Contains(connection.DeviceId) || !connection.Online)
                continue;

            devices.Add(new JsonObject
            {
                ["deviceId"] = connection.DeviceId,
                ["name"] = connection.Name,
                ["project"] = connection.Project,
                ["firmware"] = connection.Firmware,
                // What the firmware says it IS, in one line, from its hello. Null when
                // the build predates the key — a blank, not the word "unknown".
                ["description"] = connection.Hello.Description,
                ["connectedSince"] = connection.ConnectedAt,
            });
        }

        return new JsonObject { ["devices"] = devices }.ToJsonString();
    }

    /// <summary>
    /// What one device is, and everything it can be asked to do, in one answer.
    ///
    /// Two device commands, because the two halves belong to two owners in the
    /// firmware and neither is invented here: <c>system describe</c> is the device's
    /// own identity and instructions, and <c>help describe</c> is its command registry
    /// describing itself — names, descriptions, and every argument with its type,
    /// whether it is required, and what it means.
    ///
    /// Both replies are nested VERBATIM. The relay parses them only far enough to put
    /// each under a key; it adds no fields, renames nothing and drops nothing, so a
    /// fact a future firmware reports arrives here without this file learning about it.
    /// </summary>
    public async Task<string> DescribeAsync(string deviceId, CancellationToken cancellationToken)
    {
        var connection = await ConnectedAsync(deviceId, cancellationToken);

        var device = await AskAsync(connection, "system describe", cancellationToken);
        var commands = await AskAsync(connection, "help describe", cancellationToken);

        return new JsonObject
        {
            ["deviceId"] = connection.DeviceId,
            ["device"] = device,
            ["commands"] = commands,
        }.ToJsonString();
    }

    /// <summary>
    /// Runs one command on one device and hands back what it said.
    ///
    /// This is the same call the relay's own dashboard traffic takes — one session on
    /// the device's pipe, the arguments written into the envelope as JSON — so a
    /// command reached this way behaves exactly as it does from a browser.
    ///
    /// Both directions may carry a body, which is what makes the streaming half of
    /// the command surface reachable at all: a body given here is written into the
    /// session after the envelope, exactly where a handler's <c>lendInput</c> reads
    /// it, and a body coming back is shaped by <see cref="DeviceReply"/> according
    /// to the media type the DEVICE declared. The relay still does not know what any
    /// of it means: it forwards bytes and reads one header field.
    /// </summary>
    public async Task<CallToolResult> ExecuteAsync(
        string deviceId,
        string command,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        string? body,
        string? bodyBase64,
        CancellationToken cancellationToken)
    {
        var connection = await ConnectedAsync(deviceId, cancellationToken);

        if (string.IsNullOrWhiteSpace(command))
            throw new McpException("no command given");

        var request = RequestBody(body, bodyBase64);

        logger.LogInformation(
            "mcp: {DeviceId} ← {Command} ({Arguments} argument(s){Body})",
            deviceId, command, arguments?.Count ?? 0,
            request is null ? "" : $", {request.Value.Length}-byte body");

        byte[] reply;
        try
        {
            reply = await connection.CommandBytesAsync(
                command, arguments, request, cancellationToken);
        }
        catch (RelayException exception)
        {
            // A REFUSAL is the device declining on purpose — an unknown command, a
            // handler's own error — and is an answer, not a fault. Either way the
            // caller gets the device's own words rather than a stack trace.
            throw new McpException($"{command}: {exception.Message}");
        }

        return new CallToolResult { Content = DeviceReply.ToContent(reply) };
    }

    /// <summary>
    /// The request body, from whichever of the two forms the caller used.
    ///
    /// Two parameters rather than one plus an encoding flag, because the names
    /// then say which is which and a model cannot pick the wrong combination.
    /// Text is the convenient case and is what a label, a config file or any
    /// other document uses; base64 is the escape hatch that keeps arbitrary bytes
    /// possible. Passing both is a caller bug, not something to guess at.
    /// </summary>
    private static ReadOnlyMemory<byte>? RequestBody(string? body, string? bodyBase64)
    {
        if (body is not null && bodyBase64 is not null)
            throw new McpException("pass either body or bodyBase64, not both");

        if (body is not null) return Encoding.UTF8.GetBytes(body);
        if (bodyBase64 is null) return null;

        try
        {
            return Convert.FromBase64String(bodyBase64);
        }
        catch (FormatException)
        {
            throw new McpException("bodyBase64 is not valid base64");
        }
    }

    /// <summary>
    /// The gate, and the only place exposure is decided. Approved and exposed is a
    /// database question; connected is a memory question; both have to say yes.
    /// </summary>
    private async Task<DeviceConnection> ConnectedAsync(
        string deviceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new McpException("no device given");

        if (!await pairing.IsMcpExposedAsync(deviceId, cancellationToken))
            // Deliberately one message for "no such device" and "not exposed": whether
            // an id exists is not something an MCP client has been given the right to
            // find out, and the operator's own dashboard is where that question is
            // answered.
            throw new McpException(
                $"no device '{deviceId}' is exposed to MCP — check the id with the "
                + "devices tool, or ask an operator to expose it on the relay");

        var connection = registry.Find(deviceId);
        if (connection is null || !connection.Online)
            throw new McpException($"device '{deviceId}' is not connected right now");

        return connection;
    }

    /// <summary>
    /// Runs one of the two describe commands and returns its reply as a node to nest.
    ///
    /// A device that refuses the command — an older firmware that has never heard of
    /// it — is not a failed describe: the other half is still worth having, so the
    /// error takes that half's place and says which command produced it.
    /// </summary>
    private async Task<JsonNode> AskAsync(
        DeviceConnection connection, string command, CancellationToken cancellationToken)
    {
        string reply;
        try
        {
            reply = await connection.CommandAsync(command, null, cancellationToken);
        }
        catch (RelayException exception)
        {
            return new JsonObject
            {
                ["error"] = $"{command}: {exception.Message}",
                ["hint"] = "this firmware may predate the command",
            };
        }

        return Parse(Truncate(reply));
    }

    /// <summary>
    /// A device's reply as JSON where it is JSON, and as the text it actually sent
    /// where it is not. Never an exception: what a device said is a fact to report,
    /// not a contract for the relay to enforce.
    /// </summary>
    private static JsonNode Parse(string reply)
    {
        try
        {
            return JsonNode.Parse(reply) ?? JsonValue.Create(reply)!;
        }
        catch (JsonException)
        {
            return new JsonObject { ["raw"] = reply };
        }
    }

    private static string Truncate(string reply) =>
        reply.Length <= MaxReplyChars
            ? reply
            : reply[..MaxReplyChars]
              + $"\n…truncated by the relay at {MaxReplyChars} characters "
              + $"({reply.Length} in total) — ask for less at a time";
}
