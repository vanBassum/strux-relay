namespace StruxRelay.Data;

/// <summary>
/// One credential that may call this relay's MCP endpoint, created and revoked from
/// the dashboard.
///
/// What is stored is a HASH, never the token. The relay only ever has to answer "is
/// this the string we issued", which a hash answers, and the difference shows up on
/// the day somebody reads this database: a stolen backup then holds no working
/// credentials. It also decides the UI — a token is shown once, at creation, because
/// afterwards nothing on this side can produce it again.
///
/// <see cref="Hint"/> exists so a row is identifiable without being usable: enough
/// characters to match a token against the one in a config file, too few to be one.
/// </summary>
internal sealed class McpToken
{
    public string Id { get; set; } = "";

    /// <summary>What it is for, in a person's words: "Bas's laptop", "CI".</summary>
    public string Name { get; set; } = "";

    /// <summary>SHA-256 of the token, hex. The only copy the relay keeps.</summary>
    public string Hash { get; set; } = "";

    /// <summary>The token's first few characters, for telling rows apart.</summary>
    public string Hint { get; set; } = "";

    /// <summary>
    /// How this credential came to exist: <c>issued</c> for one created on the
    /// dashboard, <c>oauth</c> for one an MCP client obtained by asking a human for
    /// consent. They are the same kind of thing once issued — a bearer token that the
    /// endpoint checks and the page can revoke — so they share a table rather than
    /// each getting their own half-list in the UI.
    /// </summary>
    public string Kind { get; set; } = "issued";

    /// <summary>
    /// For an OAuth grant, the client's own name for itself ("ChatGPT"). Untrusted
    /// and display-only: a client chooses it at registration.
    /// </summary>
    public string ClientName { get; set; } = "";

    /// <summary>
    /// The resource this token was issued for (RFC 8707), and the audience check the
    /// endpoint makes on every request. A token is only good for the MCP endpoint it
    /// was asked for.
    /// </summary>
    public string Resource { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When it stops working on its own. Null for a dashboard-issued token, which is
    /// a credential a person put in a config file and which should not expire out from
    /// under them; set for an OAuth grant, which has a refresh token behind it.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// SHA-256 of the refresh token that renews this grant, or empty when there is
    /// none. Rotated on every use, as OAuth 2.1 requires for public clients: the old
    /// refresh token stops working the moment a new one is handed out, so a stolen
    /// copy is detectable by the legitimate client suddenly being logged out.
    /// </summary>
    public string RefreshHash { get; set; } = "";

    /// <summary>
    /// When this token last authenticated a request. Null until it is used once —
    /// which is itself the answer to "did that client ever actually connect".
    /// </summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// Revoked tokens are kept rather than deleted, so the page can say a credential
    /// existed and when it stopped working. A revoked row never authenticates
    /// anything.
    /// </summary>
    public DateTime? RevokedAt { get; set; }
}
