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

    public DateTime CreatedAt { get; set; }

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
