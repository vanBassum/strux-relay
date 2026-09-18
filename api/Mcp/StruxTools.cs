using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace StruxRelay.Mcp;

/// <summary>
/// The relay's whole MCP surface: three tools, none of which names a product, a
/// command or a capability.
///
/// There is deliberately NO tool per device command. A device already describes
/// itself — its commands, their arguments, their types and their meaning — so the
/// discovery path is progressive and lives on the device: list what is there, ask one
/// device what it can do, then do it. Generating a tool per command would move that
/// knowledge into the relay, where it would be a copy of something that changes every
/// time somebody flashes a board.
///
/// The descriptions below are the only instructions an MCP client gets, so they are
/// written for one: they say what to call first, what the answers contain, and what
/// the relay will refuse.
/// </summary>
[McpServerToolType]
internal sealed class StruxTools
{
    [McpServerTool(Name = "devices", ReadOnly = true, Idempotent = true)]
    [Description(
        "List the Strux devices reachable through this relay right now. Start here: "
        + "every other tool takes a deviceId from this list. Each entry carries the "
        + "device's id, the name and project its firmware reports, its firmware "
        + "version and a one-line description of what the device is. A device appears "
        + "only if an operator has approved it, switched MCP exposure on for it, and "
        + "it is connected — so an empty list means there is nothing to drive, not "
        + "that something went wrong. Call 'describe' next to find out what a device "
        + "can actually do.")]
    public static Task<string> Devices(DeviceMcp mcp, CancellationToken cancellationToken) =>
        mcp.ListAsync(cancellationToken);

    [McpServerTool(Name = "describe", ReadOnly = true, Idempotent = true)]
    [Description(
        "Ask one device what it is and everything it can do. Returns two things, both "
        + "straight from the firmware that is running: 'device' — its name, firmware "
        + "version, a short description and free-form instructions written by whoever "
        + "built it, which explain how the device is meant to be driven (workflows, "
        + "units, conventions, limitations); and 'commands' — every command grouped by "
        + "category, each with a description and its full argument list, giving each "
        + "argument's name, type (string, uint32 or bool), whether it is required, and "
        + "what it means. Read the instructions before composing calls: they are where "
        + "relationships between commands are explained. Everything here is specific to "
        + "the firmware version connected right now, so describe again after a device "
        + "has been updated.")]
    public static Task<string> Describe(
        DeviceMcp mcp,
        [Description("The device's id, exactly as the 'devices' tool reported it.")]
        string deviceId,
        CancellationToken cancellationToken) =>
        mcp.DescribeAsync(deviceId, cancellationToken);

    [McpServerTool(Name = "execute")]
    [Description(
        "Run one command on one device and return its reply. The command is "
        + "the two-word route exactly as 'describe' reports it — its category and its "
        + "name with a space between, such as 'system info', 'settings set' or "
        + "'led get'. Arguments are a JSON object of the argument names that command "
        + "declares; omit it for a command that takes none. The reply is the device's "
        + "own, unread and uninterpreted by the relay. This is a real device: "
        + "commands that write flash, change settings or reboot the board do exactly "
        + "that, immediately and without a confirmation step, so check the command's "
        + "description before calling it.\n\n"
        + "Some commands take or return a BODY — file contents, an image — as bytes "
        + "in their own right rather than as an argument, and a command's description "
        + "says when it does. To send one, pass 'body' for text (an SVG, a config "
        + "file) or 'bodyBase64' for arbitrary bytes. A reply that carries a body "
        + "comes back as two parts: the device's JSON header, then the body itself — "
        + "as text when it is text, and as an image you can look at when the device "
        + "says it is one. A long text reply is cut off with a marker saying so; "
        + "binary is never cut.")]
    public static Task<CallToolResult> Execute(
        DeviceMcp mcp,
        [Description("The device's id, exactly as the 'devices' tool reported it.")]
        string deviceId,
        [Description(
            "The command's full two-word route, e.g. 'settings list' or 'partition status'.")]
        string command,
        [Description(
            "The command's arguments as a JSON object keyed by argument name, e.g. "
            + "{\"key\": \"relay.url\", \"value\": \"ws://host/device\"}. Omit for a "
            + "command that declares no arguments.")]
        // Defaulted, not merely nullable: a nullable parameter with no default is
        // still REQUIRED in the generated schema, which would oblige a caller to pass
        // "arguments": null for every command that takes none.
        Dictionary<string, JsonElement>? arguments = null,
        [Description(
            "A TEXT body to send with the command, for commands that take their "
            + "payload as bytes rather than as an argument — the contents of a file "
            + "being written, for instance. Omit unless the command's description "
            + "says it reads a body.")]
        string? body = null,
        [Description(
            "The same, for a body that is not text: arbitrary bytes, base64 encoded. "
            + "Pass this or 'body', never both.")]
        string? bodyBase64 = null,
        CancellationToken cancellationToken = default) =>
        mcp.ExecuteAsync(deviceId, command, arguments, body, bodyBase64, cancellationToken);
}
