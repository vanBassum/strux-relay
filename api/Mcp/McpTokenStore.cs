using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StruxRelay.Data;
using StruxRelay.Hubs;
using StruxRelay.Models;

namespace StruxRelay.Mcp;

/// <summary>
/// The credentials that may call <c>/mcp</c>: minting them, listing them, revoking
/// them, and the one question the endpoint asks on every request.
///
/// Three sources, and the difference between them is who can change them:
///
///   * the DEPLOYMENT token, from configuration (<c>Relay__Mcp__Token</c>). It exists
///     before there is a database to keep anything in, it is how a fresh relay is
///     reachable at all, and it is rotated where every other secret is — in the
///     deployment's secrets file. The dashboard SHOWS it, so it is never an
///     invisible credential, but cannot revoke it: a button that silently disagreed
///     with the file on disk would be worse than no button.
///   * ISSUED tokens, from the dashboard. Named, revocable, and each one says when it
///     was last used, so "which of these is still in something's config" has an
///     answer.
///   * OAUTH grants, from a client that asked a human for consent (see
///     <see cref="McpOAuth"/>). These exist because ChatGPT will not carry a static
///     token — it discovers an authorization server and runs a code flow, or it does
///     not connect. They land in the same table as the rest, so the same page revokes
///     them and the same check verifies them.
///
/// One check, three sources — not three mechanisms. Whichever matched, the caller is
/// the same kind of caller, and which DEVICES it may then reach is the other gate
/// entirely (see <see cref="Data.ApprovedDevice.McpExposed"/>).
/// </summary>
internal sealed class McpTokenStore(
    IDbContextFactory<RelayDbContext> contexts,
    IConfiguration configuration,
    IHubContext<RelayHub> hub,
    ILogger<McpTokenStore> logger)
{
    /// <summary>
    /// A prefix a person can recognise in a config file, and a scanner can match. The
    /// random half is 32 bytes — the same 256 bits the deployment token has — in
    /// base64url, so it is shorter than hex and still safe in an HTTP header.
    /// </summary>
    private const string Prefix = "strux_mcp_";

    /// <summary>
    /// How stale a last-used stamp may get before it is written again. Without this
    /// every MCP request would be a database write, and the page cannot tell the
    /// difference between "a minute ago" and "fifty seconds ago" anyway.
    /// </summary>
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    /// <summary>The token the deployment was configured with, or "" when there is none.</summary>
    public string DeploymentToken => configuration[$"{McpOptions.Section}:Token"] ?? "";

    /// <summary>
    /// Is this the credential for anything, and for THIS endpoint. Called on every
    /// request under /mcp, so it is one indexed lookup and — at most once a minute per
    /// token — one small write.
    ///
    /// <paramref name="resource"/> is this MCP server's canonical URI. A token carrying
    /// a different one is refused even though it is otherwise valid: an OAuth grant is
    /// bound to the resource it was requested for (RFC 8707), and a resource server
    /// that skips that check is the audience-confusion hole the MCP security guidance
    /// opens with.
    /// </summary>
    public async Task<bool> VerifyAsync(
        string presented, string resource, CancellationToken cancellationToken)
    {
        if (presented.Length == 0) return false;

        // The deployment token is compared in constant time, because it is compared as
        // a STRING: how long a mismatch takes is the only thing a wrong answer would
        // otherwise leak. Issued tokens are looked up by their hash, where that
        // question does not arise — matching a hash needs a preimage, not patience.
        var deployment = DeploymentToken;
        if (deployment.Length > 0 && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(deployment), Encoding.UTF8.GetBytes(presented)))
            return true;

        var hash = HashOf(presented);

        await using var database = await contexts.CreateDbContextAsync(cancellationToken);
        var token = await database.McpTokens.FirstOrDefaultAsync(
            entry => entry.Hash == hash && entry.RevokedAt == null, cancellationToken);
        if (token is null) return false;

        var now = DateTime.UtcNow;
        if (token.ExpiresAt is not null && token.ExpiresAt <= now) return false;

        // Empty means a dashboard-issued token, which was never scoped to a resource
        // and is good for this relay's endpoint by construction.
        if (token.Resource.Length > 0
            && !string.Equals(token.Resource.TrimEnd('/'), resource.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase))
            return false;

        if (token.LastUsedAt is null || now - token.LastUsedAt > TouchInterval)
        {
            token.LastUsedAt = now;
            await database.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    // ── OAuth grants ──────────────────────────────────────────────────────────

    /// <summary>
    /// Issues the pair an OAuth client gets: an access token with a life, and a refresh
    /// token to replace it with. Both land in the same table the dashboard's own tokens
    /// live in, so one page revokes every kind of credential this relay honours.
    /// </summary>
    public async Task<McpGrant> GrantAsync(
        string clientName, string resource, string approvedBy,
        CancellationToken cancellationToken = default)
    {
        var access = Prefix + McpOAuth.Base64Url(RandomNumberGenerator.GetBytes(32));
        var refresh = "strux_rt_" + McpOAuth.Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = DateTime.UtcNow;

        var token = new McpToken
        {
            Id = Guid.NewGuid().ToString("n"),
            // The name is what the page shows, so it says who holds it and who let them
            // in — the two things somebody deciding whether to revoke it wants.
            Name = approvedBy.Length > 0 ? $"{clientName} (via {approvedBy})" : clientName,
            Kind = "oauth",
            ClientName = clientName,
            Resource = resource,
            Hash = HashOf(access),
            Hint = access[..(Prefix.Length + 4)],
            RefreshHash = HashOf(refresh),
            CreatedAt = now,
            ExpiresAt = now + McpOAuth.AccessTokenLifetime,
        };

        await using var database = await contexts.CreateDbContextAsync(cancellationToken);
        database.McpTokens.Add(token);
        await database.SaveChangesAsync(cancellationToken);
        await AnnounceAsync();

        return new McpGrant(access, refresh, token.ExpiresAt.Value);
    }

    /// <summary>
    /// Trades a refresh token for a new pair, and ROTATES it: the old refresh token
    /// stops working the moment this returns. OAuth 2.1 requires that for public
    /// clients, and the reason is worth keeping in mind — with rotation, a stolen
    /// refresh token shows up as the real client suddenly being logged out, instead of
    /// two parties quietly sharing an endless grant.
    ///
    /// Null when the token is unknown, already rotated, or revoked.
    /// </summary>
    public async Task<McpGrant?> RefreshAsync(
        string presented, CancellationToken cancellationToken = default)
    {
        var hash = HashOf(presented);

        await using var database = await contexts.CreateDbContextAsync(cancellationToken);
        var token = await database.McpTokens.FirstOrDefaultAsync(
            entry => entry.RefreshHash == hash && entry.RevokedAt == null, cancellationToken);
        if (token is null) return null;

        var access = Prefix + McpOAuth.Base64Url(RandomNumberGenerator.GetBytes(32));
        var refresh = "strux_rt_" + McpOAuth.Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = DateTime.UtcNow;

        // The SAME row is renewed rather than a new one added: to the person reading
        // the page this is one connector that keeps working, not a list that grows a
        // line every twelve hours.
        token.Hash = HashOf(access);
        token.Hint = access[..(Prefix.Length + 4)];
        token.RefreshHash = HashOf(refresh);
        token.ExpiresAt = now + McpOAuth.AccessTokenLifetime;
        token.LastUsedAt = now;
        await database.SaveChangesAsync(cancellationToken);
        await AnnounceAsync();

        return new McpGrant(access, refresh, token.ExpiresAt.Value);
    }

    /// <summary>
    /// Mints a token and hands back the ONLY copy that will ever exist. The caller
    /// shows it once; the relay keeps a hash.
    /// </summary>
    public async Task<McpTokenCreated> CreateAsync(
        string name, CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (name.Length == 0)
            return new McpTokenCreated(false, null, null, "give the token a name");
        if (name.Length > 60)
            return new McpTokenCreated(false, null, null, "that name is too long");

        var secret = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));

        var token = new McpToken
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = name,
            Hash = HashOf(secret),
            // Enough to recognise a token, nowhere near enough to be one.
            Hint = secret[..(Prefix.Length + 4)],
            CreatedAt = DateTime.UtcNow,
        };

        await using (var database = await contexts.CreateDbContextAsync(cancellationToken))
        {
            database.McpTokens.Add(token);
            await database.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("mcp: issued token '{Name}' ({Hint}…)", token.Name, token.Hint);
        await AnnounceAsync();

        return new McpTokenCreated(true, secret, View(token), null);
    }

    /// <summary>
    /// Stops a token working. Kept as a row rather than deleted, so the page can still
    /// say the credential existed and when it stopped — which is what somebody asks
    /// after revoking the wrong one.
    /// </summary>
    public async Task<McpTokenResult> RevokeAsync(
        string id, CancellationToken cancellationToken = default)
    {
        await using var database = await contexts.CreateDbContextAsync(cancellationToken);

        var token = await database.McpTokens.FirstOrDefaultAsync(
            entry => entry.Id == id, cancellationToken);
        if (token is null)
            return new McpTokenResult(false, "no such token");

        if (token.RevokedAt is null)
        {
            token.RevokedAt = DateTime.UtcNow;
            await database.SaveChangesAsync(cancellationToken);
            logger.LogInformation("mcp: revoked token '{Name}'", token.Name);
            await AnnounceAsync();
        }

        return new McpTokenResult(true);
    }

    /// <summary>Forgets a revoked token's row entirely. Refuses a live one.</summary>
    public async Task<McpTokenResult> ForgetAsync(
        string id, CancellationToken cancellationToken = default)
    {
        await using var database = await contexts.CreateDbContextAsync(cancellationToken);

        var token = await database.McpTokens.FirstOrDefaultAsync(
            entry => entry.Id == id, cancellationToken);
        if (token is null)
            return new McpTokenResult(false, "no such token");
        if (token.RevokedAt is null)
            return new McpTokenResult(false, "revoke it first");

        database.McpTokens.Remove(token);
        await database.SaveChangesAsync(cancellationToken);
        await AnnounceAsync();
        return new McpTokenResult(true);
    }

    /// <summary>
    /// Everything the MCP page draws: whether the endpoint is usable at all, whether a
    /// deployment token is configured, and every issued token.
    /// </summary>
    public async Task<McpView> ViewAsync(CancellationToken cancellationToken = default)
    {
        await using var database = await contexts.CreateDbContextAsync(cancellationToken);

        var tokens = await database.McpTokens
            .OrderByDescending(token => token.CreatedAt)
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var live = tokens.Count(
            token => token.RevokedAt == null && (token.ExpiresAt is null || token.ExpiresAt > now));
        var configured = DeploymentToken.Length > 0;

        return new McpView(
            // What the endpoint will actually do, rather than what is configured: with
            // no credential of any kind it refuses everything, and the page has to say
            // so rather than leave somebody wondering why their client gets a 503.
            Enabled: configured || live > 0,
            DeploymentTokenConfigured: configured,
            [.. tokens.Select(View)]);
    }

    private static McpTokenView View(McpToken token) =>
        new(token.Id, token.Name, token.Hint, token.CreatedAt, token.LastUsedAt,
            token.RevokedAt, token.Kind, token.ExpiresAt);

    private static string HashOf(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>
    /// base64url without padding: safe in a header, in a URL and in a shell, which a
    /// plain base64 '+' or '/' is not reliably.
    /// </summary>
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private Task AnnounceAsync() => hub.Clients.All.SendAsync("McpTokensChanged");
}
