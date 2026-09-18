using System.Security.Cryptography;
using System.Text;

namespace StruxRelay.Mcp;

/// <summary>
/// The one machine credential that guards <c>/mcp</c>.
///
/// Every other browser-facing path on this host sits behind the reverse proxy's
/// forward-auth, which answers an unauthenticated request with a REDIRECT to a login
/// flow. That is right for a person and useless for an MCP client: a program following
/// a 302 to an OAuth screen learns nothing and reports nothing useful. So <c>/mcp</c>
/// is routed past forward-auth at the proxy and checks a bearer token here instead —
/// in the process that owns the thing being protected, rather than in a label on a
/// container.
///
/// Deliberately ONE token, and no user model behind it. There are no accounts, scopes,
/// refresh tokens or OAuth here, because there is nothing for them to describe: the
/// question this answers is "is this the machine we gave the credential to", and WHICH
/// devices it may then reach is a different boundary entirely (see
/// <see cref="Data.ApprovedDevice.McpExposed"/>). Two simple gates in series beat one
/// elaborate one.
/// </summary>
internal sealed class McpOptions
{
    public const string Section = "Relay:Mcp";

    /// <summary>
    /// The shared secret, from configuration — <c>Relay__Mcp__Token</c> in the
    /// environment, which is how the deployment passes it in from its secrets file.
    /// Never a default and never a literal: an unset token does not mean "open", it
    /// means the endpoint is not configured (see below).
    /// </summary>
    public string Token { get; set; } = "";
}

internal static class McpBearerToken
{
    /// <summary>
    /// Requires a bearer token on every request under <paramref name="path"/>.
    ///
    /// MIDDLEWARE on the whole prefix, not a filter on one endpoint, and that is the
    /// point: streamable HTTP is several requests — the POST that carries a call, the
    /// GET that holds the event stream open, the DELETE that ends a session — and the
    /// SDK maps sub-paths (<c>/mcp/sse</c>, <c>/mcp/message</c>) under the same prefix.
    /// Authenticating only the first request would leave the stream that carries the
    /// answers open to anyone who asked for it.
    ///
    /// Must be registered BEFORE the endpoints it guards.
    /// </summary>
    public static WebApplication UseMcpBearerToken(this WebApplication app, string path)
    {
        var token = app.Configuration[$"{McpOptions.Section}:Token"] ?? "";
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(McpBearerToken).FullName!);

        if (token.Length == 0)
        {
            // Closed, not open. A deployment that forgot the secret gets an endpoint
            // that refuses everything and says why, rather than a device-driving API
            // published to the internet by omission — which is the failure mode worth
            // designing against, because nothing about it looks wrong from outside.
            logger.LogWarning(
                "MCP is unconfigured: no Relay__Mcp__Token, so {Path} refuses every "
                + "request. Set the token to enable it.", path);
        }
        else
        {
            logger.LogInformation("MCP bearer token is configured; {Path} is open to it", path);
        }

        var expected = Encoding.UTF8.GetBytes(token);

        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(path),
            branch => branch.Use(async (context, next) =>
            {
                if (expected.Length == 0)
                {
                    await Refuse(context, StatusCodes.Status503ServiceUnavailable,
                        "MCP is not configured on this relay");
                    return;
                }

                if (!Presented(context, out var presented))
                {
                    await Refuse(context, StatusCodes.Status401Unauthorized,
                        "an Authorization: Bearer token is required");
                    return;
                }

                // Constant time, for the same reason the device token is compared that
                // way: how long a mismatch takes is the one thing a wrong answer would
                // otherwise tell somebody guessing.
                if (!CryptographicOperations.FixedTimeEquals(expected, presented))
                {
                    logger.LogWarning(
                        "mcp: refused a request from {Address} with a bad token",
                        context.Connection.RemoteIpAddress);
                    await Refuse(context, StatusCodes.Status401Unauthorized,
                        "that bearer token is not valid here");
                    return;
                }

                await next(context);
            }));

        return app;
    }

    /// <summary>
    /// The presented token's bytes, or false when the header is absent or is not a
    /// bearer one. Case-insensitive on the scheme, because RFC 7235 says it is.
    /// </summary>
    private static bool Presented(HttpContext context, out byte[] token)
    {
        token = [];

        var header = context.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        var value = header[scheme.Length..].Trim();
        if (value.Length == 0)
            return false;

        token = Encoding.UTF8.GetBytes(value);
        return true;
    }

    /// <summary>
    /// A refusal an MCP client can read: a status, a plain sentence, and — the part
    /// that matters here — never a redirect. Being bounced to a login page is exactly
    /// what this endpoint exists to avoid.
    /// </summary>
    private static Task Refuse(HttpContext context, int status, string reason)
    {
        context.Response.StatusCode = status;
        if (status == StatusCodes.Status401Unauthorized)
            context.Response.Headers.WWWAuthenticate = "Bearer";
        context.Response.ContentType = "text/plain";
        return context.Response.WriteAsync(reason + "\n");
    }
}
