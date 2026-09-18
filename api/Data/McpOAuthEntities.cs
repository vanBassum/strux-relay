namespace StruxRelay.Data;

/// <summary>
/// An OAuth client that registered itself with this relay — ChatGPT, Claude, or any
/// other MCP client that found the endpoint and followed the discovery documents.
///
/// Registration is open (RFC 7591 dynamic client registration) and that is not a hole:
/// a client_id is not a credential here. It grants nothing on its own — every flow
/// still ends with a human, already authenticated by the proxy, pressing Allow on the
/// consent page. What registration buys is that a client the relay has never heard of
/// can start the conversation without somebody copying identifiers between two
/// browser tabs.
/// </summary>
internal sealed class McpOAuthClient
{
    public string ClientId { get; set; } = "";

    /// <summary>What the client called itself. Shown on the consent screen, so it is
    /// displayed as the untrusted string it is — a name, not a verified identity.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The exact redirect URIs this client registered, newline separated. Matched
    /// EXACTLY at authorize time: a prefix match here is the open-redirect hole every
    /// OAuth security document opens with.
    /// </summary>
    public string RedirectUris { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    public IEnumerable<string> Redirects() =>
        RedirectUris.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// One authorization code, in flight between the consent screen and the token
/// endpoint.
///
/// Short-lived, single-use, and bound to everything it was issued for: the client, the
/// exact redirect URI, the PKCE challenge and the resource. A code that is redeemed
/// twice, redeemed late, or redeemed with a different verifier is not a code — the
/// checks are cheap and each one closes a documented attack.
/// </summary>
internal sealed class McpAuthCode
{
    /// <summary>SHA-256 of the code. The code itself is never stored, like the tokens.</summary>
    public string Hash { get; set; } = "";

    public string ClientId { get; set; } = "";

    public string RedirectUri { get; set; } = "";

    /// <summary>The S256 code challenge. Plain PKCE is not accepted.</summary>
    public string CodeChallenge { get; set; } = "";

    /// <summary>
    /// The resource the client said it wanted the token for (RFC 8707). Recorded so
    /// the token is bound to it, which is what stops a token issued here from being
    /// replayed at some other service — or one issued elsewhere from working here.
    /// </summary>
    public string Resource { get; set; } = "";

    public string Scope { get; set; } = "";

    /// <summary>Who pressed Allow, as the proxy reported them. For the audit line.</summary>
    public string ApprovedBy { get; set; } = "";

    public DateTime ExpiresAt { get; set; }
}
