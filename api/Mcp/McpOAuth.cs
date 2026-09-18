using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using StruxRelay.Data;
using StruxRelay.Models;

namespace StruxRelay.Mcp;

/// <summary>
/// The relay's own OAuth 2.1 authorization server, existing for exactly one reason:
/// ChatGPT will not carry a static token.
///
/// A bearer token in a header is the whole of what this endpoint needs, and it is what
/// Claude Code and a curl command use. ChatGPT's connector UI does not offer it — it
/// discovers an authorization server from the MCP endpoint and runs an authorization
/// code flow, or it does not connect at all. So the relay grows the smallest thing that
/// satisfies that: authorize, token, register, and the two discovery documents.
///
/// What makes it small is that the relay is BOTH the authorization server and the
/// resource server, and that it does not authenticate people. The consent page sits on
/// the proxy-authenticated side of the host, so by the time it renders, Authentik has
/// already established who is there — exactly as it does for the dashboard. This server
/// therefore never sees a password, never stores a user, and issues opaque tokens it
/// can check against its own table rather than JWTs it would have to sign and rotate
/// keys for. Same table as the dashboard's own tokens, so one page revokes both.
///
/// The parts that are not negotiable, because each one closes a documented attack:
///   * PKCE with S256 — plain is refused;
///   * exact redirect-URI matching against what the client registered;
///   * single-use codes with a one-minute life;
///   * the RFC 8707 `resource` recorded on the grant and checked on every request, so
///     a token minted here is only good here;
///   * rotating refresh tokens, as OAuth 2.1 requires for public clients.
/// </summary>
internal static class McpOAuth
{
    /// <summary>The scope this server issues. One, because there is one thing to grant.</summary>
    public const string Scope = "mcp";

    /// <summary>
    /// A code lives just long enough to be redeemed by the client that just received
    /// it. Anything longer is a window for a code that leaked through a referrer,
    /// a log or a shoulder.
    /// </summary>
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Access-token life. The spec says short; the practical floor is set by what
    /// breaks when a refresh does not happen — a connector that silently stops working
    /// is worse for this deployment than a token that lives half a day. Twelve hours
    /// with rotating refresh is the compromise, and a revoke from the dashboard is
    /// immediate either way, because every request is checked against the table.
    /// </summary>
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(12);

    public static void MapMcpOAuth(this WebApplication app, string mcpPath)
    {
        // ── Discovery ─────────────────────────────────────────────────────────
        //
        // Two documents, and MCP clients fetch them in this order: the protected
        // resource says which authorization server to ask, the authorization server
        // says where its endpoints are. Both are served under the path-suffixed
        // spellings too (RFC 9728 §3.1, RFC 8414 §3.1), because a client deriving the
        // URL from a resource with a path — which /mcp is — will ask for those.

        app.MapGet("/.well-known/oauth-protected-resource", (HttpContext context) =>
            ProtectedResource(context, mcpPath));
        app.MapGet("/.well-known/oauth-protected-resource/{**rest}", (HttpContext context) =>
            ProtectedResource(context, mcpPath));

        app.MapGet("/.well-known/oauth-authorization-server", AuthorizationServer);
        app.MapGet("/.well-known/oauth-authorization-server/{**rest}", AuthorizationServer);
        // Some clients look for the OpenID document instead, and the fields they need
        // are the same ones. Answering it costs a line and saves a failed discovery.
        app.MapGet("/.well-known/openid-configuration", AuthorizationServer);

        // ── Dynamic client registration (RFC 7591) ────────────────────────────
        app.MapPost("/oauth/register", RegisterAsync);

        // ── The flow ──────────────────────────────────────────────────────────
        //
        // GET renders consent, POST grants it. This pair is the ONLY part of the OAuth
        // surface that belongs behind the proxy's forward-auth: it is the step a human
        // performs, and Authentik having already identified them is what the relay
        // relies on instead of having accounts of its own.
        app.MapGet("/oauth/authorize", AuthorizeAsync);
        app.MapPost("/oauth/authorize", ApproveAsync);

        app.MapPost("/oauth/token", TokenAsync);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Discovery documents
    // ──────────────────────────────────────────────────────────────────────────

    private static IResult ProtectedResource(HttpContext context, string mcpPath)
    {
        var issuer = PublicOrigin(context);
        return Results.Json(new Dictionary<string, object>
        {
            // The canonical URI of this MCP server, and the value a client must send as
            // `resource`. No trailing slash, per the spec's own guidance.
            ["resource"] = issuer + mcpPath,
            ["authorization_servers"] = new[] { issuer },
            ["scopes_supported"] = new[] { Scope },
            ["bearer_methods_supported"] = new[] { "header" },
        });
    }

    private static IResult AuthorizationServer(HttpContext context)
    {
        var issuer = PublicOrigin(context);
        return Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = issuer,
            ["authorization_endpoint"] = issuer + "/oauth/authorize",
            ["token_endpoint"] = issuer + "/oauth/token",
            ["registration_endpoint"] = issuer + "/oauth/register",
            ["response_types_supported"] = new[] { "code" },
            ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
            // S256 only. Advertising "plain" would be advertising a downgrade.
            ["code_challenge_methods_supported"] = new[] { "S256" },
            // Public clients: there is no client secret to present, because a client
            // that registered itself from the internet cannot keep one.
            ["token_endpoint_auth_methods_supported"] = new[] { "none" },
            ["scopes_supported"] = new[] { Scope },
            ["resource_indicators_supported"] = true,
        });
    }

    /// <summary>
    /// The origin this relay is reached at from outside, which is not what Kestrel
    /// sees: TLS ends at Traefik, so without the forwarded headers every URL in these
    /// documents would say <c>http://</c> and the whole flow would be refused for
    /// being insecure. <c>Relay:PublicUrl</c> overrides it for a deployment whose proxy
    /// does not forward them.
    /// </summary>
    private static string PublicOrigin(HttpContext context)
    {
        var configured = context.RequestServices
            .GetRequiredService<IConfiguration>()["Relay:PublicUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.TrimEnd('/');

        return $"{context.Request.Scheme}://{context.Request.Host}";
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Registration
    // ──────────────────────────────────────────────────────────────────────────

    private sealed record RegistrationRequest(
        [property: JsonPropertyName("client_name")] string? ClientName,
        [property: JsonPropertyName("redirect_uris")] string[]? RedirectUris);

    private static async Task<IResult> RegisterAsync(
        HttpContext context,
        IDbContextFactory<RelayDbContext> contexts,
        ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger(typeof(McpOAuth).FullName!);

        RegistrationRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<RegistrationRequest>();
        }
        catch (JsonException)
        {
            return Error(400, "invalid_client_metadata", "the registration body is not JSON");
        }

        var redirects = request?.RedirectUris ?? [];
        if (redirects.Length == 0)
            return Error(400, "invalid_redirect_uri", "redirect_uris is required");

        foreach (var redirect in redirects)
        {
            // Loopback or HTTPS, nothing else — OAuth 2.1's own rule, and the one that
            // keeps a registration from turning this into an open redirector to plain
            // HTTP somewhere.
            if (!Uri.TryCreate(redirect, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
                return Error(400, "invalid_redirect_uri",
                    $"'{redirect}' must be https or loopback");
        }

        var client = new McpOAuthClient
        {
            ClientId = Guid.NewGuid().ToString("n"),
            Name = Clean(request?.ClientName) is { Length: > 0 } name ? name : "an MCP client",
            RedirectUris = string.Join('\n', redirects),
            CreatedAt = DateTime.UtcNow,
        };

        await using (var database = await contexts.CreateDbContextAsync(context.RequestAborted))
        {
            database.McpOAuthClients.Add(client);
            await database.SaveChangesAsync(context.RequestAborted);
        }

        logger.LogInformation(
            "mcp oauth: '{Name}' registered as {ClientId}", client.Name, client.ClientId);

        return Results.Json(new Dictionary<string, object>
        {
            ["client_id"] = client.ClientId,
            ["client_id_issued_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["client_name"] = client.Name,
            ["redirect_uris"] = redirects,
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["token_endpoint_auth_method"] = "none",
        }, statusCode: 201);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Authorize + consent
    // ──────────────────────────────────────────────────────────────────────────

    private static async Task<IResult> AuthorizeAsync(
        HttpContext context,
        IDbContextFactory<RelayDbContext> contexts)
    {
        var query = context.Request.Query;
        var clientId = query["client_id"].ToString();
        var redirectUri = query["redirect_uri"].ToString();

        await using var database = await contexts.CreateDbContextAsync(context.RequestAborted);
        var client = await database.McpOAuthClients.FirstOrDefaultAsync(
            entry => entry.ClientId == clientId, context.RequestAborted);

        // Everything about the CLIENT is checked before anything is redirected
        // anywhere. An unknown client or an unregistered redirect URI is answered on
        // this page, never by bouncing the browser at the URI in question — which is
        // the whole open-redirect class.
        if (client is null)
            return Html(400, Page("Unknown client",
                "That client is not registered with this relay. Remove the connector and add it again."));

        if (!client.Redirects().Contains(redirectUri, StringComparer.Ordinal))
            return Html(400, Page("Redirect mismatch",
                "That redirect URI is not one this client registered. Nothing has been granted."));

        // From here failures CAN go back to the client, because the destination is
        // known-good.
        if (query["response_type"].ToString() != "code")
            return Redirect(redirectUri, query["state"], "unsupported_response_type");
        if (query["code_challenge_method"].ToString() != "S256")
            return Redirect(redirectUri, query["state"], "invalid_request",
                "PKCE with S256 is required");
        if (query["code_challenge"].ToString().Length == 0)
            return Redirect(redirectUri, query["state"], "invalid_request",
                "code_challenge is required");

        return Html(200, ConsentPage(context, client, query));
    }

    private static async Task<IResult> ApproveAsync(
        HttpContext context,
        IDbContextFactory<RelayDbContext> contexts,
        ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger(typeof(McpOAuth).FullName!);
        var form = await context.Request.ReadFormAsync(context.RequestAborted);

        var clientId = form["client_id"].ToString();
        var redirectUri = form["redirect_uri"].ToString();
        var state = form["state"].ToString();

        await using var database = await contexts.CreateDbContextAsync(context.RequestAborted);
        var client = await database.McpOAuthClients.FirstOrDefaultAsync(
            entry => entry.ClientId == clientId, context.RequestAborted);

        // Re-validated rather than trusted from the form: the browser is the thing
        // that carried these values, and it is the thing being defended against.
        if (client is null || !client.Redirects().Contains(redirectUri, StringComparer.Ordinal))
            return Html(400, Page("Redirect mismatch", "Nothing has been granted."));

        if (form["decision"].ToString() != "allow")
            return Redirect(redirectUri, state, "access_denied");

        var code = Secret();
        database.McpAuthCodes.Add(new McpAuthCode
        {
            Hash = HashOf(code),
            ClientId = clientId,
            RedirectUri = redirectUri,
            CodeChallenge = form["code_challenge"].ToString(),
            // The resource the client asked for, or this relay's own MCP endpoint when
            // it asked for none. Recorded either way, because it is what the token is
            // bound to.
            Resource = form["resource"].ToString() is { Length: > 0 } resource
                ? resource
                : PublicOrigin(context) + "/mcp",
            Scope = Scope,
            ApprovedBy = Person(context),
            ExpiresAt = DateTime.UtcNow + CodeLifetime,
        });

        // Codes are tiny and short-lived, so the table is swept here rather than by a
        // background job: anything expired is gone the next time somebody consents.
        var now = DateTime.UtcNow;
        await database.McpAuthCodes.Where(entry => entry.ExpiresAt < now)
            .ExecuteDeleteAsync(context.RequestAborted);
        await database.SaveChangesAsync(context.RequestAborted);

        logger.LogInformation(
            "mcp oauth: {Person} allowed '{Client}'", Person(context), client.Name);

        var target = new UriBuilder(redirectUri);
        target.Query = Append(target.Query, $"code={Uri.EscapeDataString(code)}"
            + (state.Length > 0 ? $"&state={Uri.EscapeDataString(state)}" : ""));
        return Results.Redirect(target.Uri.ToString());
    }

    /// <summary>
    /// Who is consenting, as the proxy reported them. Display only — the relay has no
    /// accounts, and this is on the page so the person can see which identity the
    /// grant is being made under, not so anything is decided by it.
    /// </summary>
    private static string Person(HttpContext context)
    {
        foreach (var header in new[]
                 { "X-authentik-username", "X-Forwarded-Preferred-Username", "X-Forwarded-User" })
        {
            var value = context.Request.Headers[header].ToString();
            if (value.Length > 0) return Clean(value);
        }
        return "";
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Token
    // ──────────────────────────────────────────────────────────────────────────

    private static async Task<IResult> TokenAsync(
        HttpContext context,
        IDbContextFactory<RelayDbContext> contexts,
        McpTokenStore tokens,
        ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger(typeof(McpOAuth).FullName!);
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var grant = form["grant_type"].ToString();

        await using var database = await contexts.CreateDbContextAsync(context.RequestAborted);

        if (grant == "authorization_code")
        {
            var code = form["code"].ToString();
            var verifier = form["code_verifier"].ToString();
            if (code.Length == 0 || verifier.Length == 0)
                return Error(400, "invalid_request", "code and code_verifier are required");

            var hash = HashOf(code);
            var record = await database.McpAuthCodes.FirstOrDefaultAsync(
                entry => entry.Hash == hash, context.RequestAborted);

            // Single use: the row goes whether or not the rest of the checks pass, so a
            // code that was replayed is dead either way.
            if (record is not null)
            {
                database.McpAuthCodes.Remove(record);
                await database.SaveChangesAsync(context.RequestAborted);
            }

            if (record is null || record.ExpiresAt < DateTime.UtcNow)
                return Error(400, "invalid_grant", "that code is not valid");
            if (record.ClientId != form["client_id"].ToString())
                return Error(400, "invalid_grant", "that code belongs to another client");
            if (form["redirect_uri"].ToString() is { Length: > 0 } redirect
                && redirect != record.RedirectUri)
                return Error(400, "invalid_grant", "redirect_uri does not match");
            if (!VerifyPkce(record.CodeChallenge, verifier))
                return Error(400, "invalid_grant", "the code_verifier does not match");

            var client = await database.McpOAuthClients.FirstOrDefaultAsync(
                entry => entry.ClientId == record.ClientId, context.RequestAborted);

            var issued = await tokens.GrantAsync(
                client?.Name ?? "an MCP client", record.Resource, record.ApprovedBy,
                context.RequestAborted);

            logger.LogInformation(
                "mcp oauth: issued a token to '{Client}'", client?.Name ?? "an MCP client");
            return TokenResponse(issued);
        }

        if (grant == "refresh_token")
        {
            var presented = form["refresh_token"].ToString();
            if (presented.Length == 0)
                return Error(400, "invalid_request", "refresh_token is required");

            var refreshed = await tokens.RefreshAsync(presented, context.RequestAborted);
            if (refreshed is null)
                return Error(400, "invalid_grant", "that refresh token is not valid");

            return TokenResponse(refreshed);
        }

        return Error(400, "unsupported_grant_type", $"'{grant}' is not supported here");
    }

    private static IResult TokenResponse(McpGrant grant) =>
        Results.Json(new Dictionary<string, object>
        {
            ["access_token"] = grant.AccessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = (int)(grant.ExpiresAt - DateTime.UtcNow).TotalSeconds,
            ["refresh_token"] = grant.RefreshToken,
            ["scope"] = Scope,
        });

    /// <summary>S256, and only S256: BASE64URL(SHA256(verifier)) must equal the challenge.</summary>
    private static bool VerifyPkce(string challenge, string verifier)
    {
        var computed = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(challenge));
    }

    public static TimeSpan AccessTokenLifetime => TokenLifetime;

    /// <summary>
    /// This MCP server's canonical URI: what a client must send as `resource`, what a
    /// token is bound to, and what the protected-resource document advertises. One
    /// function, so those three can never disagree.
    /// </summary>
    public static string CanonicalResource(HttpContext context, string mcpPath) =>
        PublicOrigin(context) + mcpPath;

    /// <summary>Where a client with no token is told to look (RFC 9728).</summary>
    public static string ResourceMetadataUrl(HttpContext context) =>
        PublicOrigin(context) + "/.well-known/oauth-protected-resource";

    // ──────────────────────────────────────────────────────────────────────────
    // Plumbing
    // ──────────────────────────────────────────────────────────────────────────

    private static string Secret() => Base64Url(RandomNumberGenerator.GetBytes(32));

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string HashOf(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Append(string query, string extra) =>
        query.Length <= 1 ? "?" + extra : query + "&" + extra;

    private static IResult Redirect(
        string redirectUri, string? state, string error, string? description = null)
    {
        var target = new UriBuilder(redirectUri);
        var parameters = $"error={Uri.EscapeDataString(error)}";
        if (description is not null)
            parameters += $"&error_description={Uri.EscapeDataString(description)}";
        if (!string.IsNullOrEmpty(state))
            parameters += $"&state={Uri.EscapeDataString(state)}";
        target.Query = Append(target.Query, parameters);
        return Results.Redirect(target.Uri.ToString());
    }

    private static IResult Error(int status, string error, string description) =>
        Results.Json(new { error, error_description = description }, statusCode: status);

    private static IResult Html(int status, string html) =>
        Results.Content(html, "text/html; charset=utf-8", statusCode: status);

    /// <summary>
    /// Strips anything that would let an untrusted string — a client's chosen name, a
    /// username from a header — become markup on the consent page. Text only, and
    /// short.
    /// </summary>
    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = new string(value.Where(c => !char.IsControl(c)).Take(80).ToArray());
        return text
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&#39;");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // The one page this server draws
    //
    // Server-rendered rather than part of the React dashboard, and that is deliberate:
    // the consent screen is the security-relevant surface of this whole flow, so it
    // does not share a bundle, a router or a state store with anything else. It also
    // has to work when the SPA does not.
    // ──────────────────────────────────────────────────────────────────────────

    private static string Page(string title, string body) => $"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>{title} — Strux relay</title>{Style}</head>
        <body><main><h1>{title}</h1><p>{body}</p></main></body></html>
        """;

    private static string ConsentPage(
        HttpContext context, McpOAuthClient client, IQueryCollection query)
    {
        var person = Person(context);
        var resource = query["resource"].ToString();

        return $"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Connect {Clean(client.Name)} — Strux relay</title>{Style}</head>
            <body><main>
              <h1>Connect {Clean(client.Name)}?</h1>
              <p class="lead">
                It is asking to drive the devices on this relay through its MCP endpoint.
              </p>
              <ul>
                <li>It will be able to <strong>list, describe and command</strong> every
                    device that is exposed to MCP here — and only those.</li>
                <li>Commands reach real hardware. Some of them write flash or reboot a
                    board.</li>
                <li>You can revoke this at any time on the relay's MCP page.</li>
              </ul>
              <dl>
                <dt>Client</dt><dd>{Clean(client.Name)}<span class="hint"> — it chose that name itself</span></dd>
                {(resource.Length > 0 ? $"<dt>For</dt><dd>{Clean(resource)}</dd>" : "")}
                {(person.Length > 0 ? $"<dt>Signed in as</dt><dd>{person}</dd>" : "")}
              </dl>
              <form method="post">
                <input type="hidden" name="client_id" value="{Clean(query["client_id"])}">
                <input type="hidden" name="redirect_uri" value="{Clean(query["redirect_uri"])}">
                <input type="hidden" name="state" value="{Clean(query["state"])}">
                <input type="hidden" name="code_challenge" value="{Clean(query["code_challenge"])}">
                <input type="hidden" name="resource" value="{Clean(resource)}">
                <div class="row">
                  <button type="submit" name="decision" value="deny" class="ghost">Cancel</button>
                  <button type="submit" name="decision" value="allow" class="primary">Allow</button>
                </div>
              </form>
            </main></body></html>
            """;
    }

    /// <summary>
    /// Inline, because this page must not depend on the dashboard's bundle. Follows the
    /// browser's colour scheme rather than the dashboard's toggle — there is no session
    /// here to have a preference in.
    /// </summary>
    private const string Style = """
        <style>
          :root { color-scheme: light dark; --fg:#18181b; --muted:#71717a; --bg:#fafafa;
                  --card:#fff; --line:#e4e4e7; --accent:#18181b; --accent-fg:#fff; }
          @media (prefers-color-scheme: dark) {
            :root { --fg:#fafafa; --muted:#a1a1aa; --bg:#09090b; --card:#18181b;
                    --line:#27272a; --accent:#fafafa; --accent-fg:#18181b; }
          }
          * { box-sizing: border-box; }
          body { margin:0; min-height:100vh; display:grid; place-items:center;
                 background:var(--bg); color:var(--fg); padding:24px;
                 font:15px/1.55 ui-sans-serif,system-ui,-apple-system,"Segoe UI",sans-serif; }
          main { width:100%; max-width:30rem; background:var(--card);
                 border:1px solid var(--line); border-radius:12px; padding:28px; }
          h1 { margin:0 0 8px; font-size:1.25rem; }
          .lead { margin:0 0 16px; color:var(--muted); }
          ul { margin:0 0 20px; padding-left:20px; color:var(--muted); }
          li { margin:6px 0; }
          li strong { color:var(--fg); }
          dl { display:grid; grid-template-columns:auto 1fr; gap:6px 14px; margin:0 0 24px;
               padding:14px; border:1px solid var(--line); border-radius:8px; font-size:13px; }
          dt { color:var(--muted); }
          dd { margin:0; word-break:break-all; }
          .hint { color:var(--muted); }
          .row { display:flex; gap:10px; justify-content:flex-end; }
          button { font:inherit; padding:9px 18px; border-radius:8px; cursor:pointer;
                   border:1px solid var(--line); }
          .ghost { background:transparent; color:var(--fg); }
          .primary { background:var(--accent); color:var(--accent-fg); border-color:var(--accent);
                     font-weight:600; }
        </style>
        """;
}
