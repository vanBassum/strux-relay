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
/// Two sources, on purpose, and the difference between them is who can change them:
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
///
/// One check, two sources — not two mechanisms. Whichever matched, the caller is the
/// same kind of caller, and which DEVICES it may then reach is the other gate
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
    /// Is this the credential for anything. Called on every request under /mcp, so it
    /// is one indexed lookup and — at most once a minute per token — one small write.
    /// </summary>
    public async Task<bool> VerifyAsync(string presented, CancellationToken cancellationToken)
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
        if (token.LastUsedAt is null || now - token.LastUsedAt > TouchInterval)
        {
            token.LastUsedAt = now;
            await database.SaveChangesAsync(cancellationToken);
        }

        return true;
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

        var live = tokens.Count(token => token.RevokedAt == null);
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
        new(token.Id, token.Name, token.Hint, token.CreatedAt, token.LastUsedAt, token.RevokedAt);

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
