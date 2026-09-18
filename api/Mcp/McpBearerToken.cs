namespace StruxRelay.Mcp;

/// <summary>
/// Where the deployment's own MCP credential comes from.
///
/// Every other browser-facing path on this host sits behind the reverse proxy's
/// forward-auth, which answers an unauthenticated request with a REDIRECT to a login
/// flow. That is right for a person and useless for an MCP client: a program following
/// a 302 to an OAuth screen learns nothing and reports nothing useful. So <c>/mcp</c>
/// is routed past forward-auth at the proxy and checks a bearer token here instead —
/// in the process that owns the thing being protected, rather than in a label on a
/// container.
///
/// This one comes from configuration, which is what makes it the credential that works
/// before anybody has opened the dashboard. Tokens issued FROM the dashboard live in
/// the database beside it (see <see cref="McpTokenStore"/>); both answer the same
/// question, and it is a small one — "is this a machine we gave a credential to".
/// There are no accounts, scopes, refresh tokens or OAuth behind it, because WHICH
/// devices that machine may then reach is a different boundary entirely (see
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
    /// Requires a valid bearer token on every request under <paramref name="path"/> —
    /// the deployment's, or one issued from the dashboard. With neither configured
    /// nor issued, nothing verifies and every request is refused, which is the right
    /// way round: an unset secret must not publish a device-driving API by omission.
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
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(McpBearerToken).FullName!);
        var tokens = app.Services.GetRequiredService<McpTokenStore>();

        logger.LogInformation(
            "MCP is guarded at {Path}: {Deployment}, plus any token issued from the "
            + "dashboard. With none of either it refuses every request.",
            path,
            tokens.DeploymentToken.Length > 0
                ? "a deployment token is configured"
                : "no deployment token (Relay__Mcp__Token is unset)");

        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(path),
            branch => branch.Use(async (context, next) =>
            {
                if (!Presented(context, out var presented))
                {
                    await Refuse(context, StatusCodes.Status401Unauthorized,
                        "an Authorization: Bearer token is required");
                    return;
                }

                if (!await tokens.VerifyAsync(presented, context.RequestAborted))
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
    /// The presented token, or false when the header is absent or is not a bearer one.
    /// Case-insensitive on the scheme, because RFC 7235 says it is.
    /// </summary>
    private static bool Presented(HttpContext context, out string token)
    {
        token = "";

        var header = context.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        var value = header[scheme.Length..].Trim();
        if (value.Length == 0)
            return false;

        token = value;
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
