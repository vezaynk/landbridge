namespace Landbridge.ControlPlane.Auth;

/// <summary>
/// One issued OAuth 2.1 authorization code (spec §5). Like every other credential
/// in the system it is opaque and only its SHA-256 lands here (§5, §13): the
/// <c>lbr_c_</c> plaintext exists once, in the 302 to the client's redirect URI.
///
/// <para>The code is bound at mint to everything the token exchange must re-check
/// (RFC 6749 §4.1.3, RFC 7636 §4.6, RFC 8707 §2): the CIMD <c>client_id</c>, the
/// exact <c>redirect_uri</c> it was issued against, the S256 <c>code_challenge</c>,
/// and the canonical <c>resource</c> (audience) if the client sent one. It is
/// <b>single-use</b> and short-lived: consumption is an atomic conditional update
/// (see <see cref="OAuthAuthorizationCodeService.ConsumeAsync"/>), so a replay or
/// a concurrent double-exchange can never both win.</para>
/// </summary>
public sealed class OAuthAuthorizationCodeRow
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 of the opaque <c>lbr_c_</c> code; the code itself is never stored (§5).</summary>
    public string CodeHash { get; set; } = "";

    /// <summary>The CIMD client identifier (an HTTPS URL) the code was issued to.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>The exact redirect URI validated at authorize; the token exchange must match it (RFC 6749 §4.1.3).</summary>
    public string RedirectUri { get; set; } = "";

    /// <summary>The RFC 7636 S256 code challenge; verified against the presented verifier at exchange.</summary>
    public string CodeChallenge { get; set; } = "";

    /// <summary>
    /// The canonical RFC 8707 resource (audience) the client bound the code to, or
    /// null if it sent none. When present the token request must echo the same
    /// value; single instance = single audience, so equality is the whole check.
    /// </summary>
    public string? Resource { get; set; }

    /// <summary>The coarse scope requested, echoed for the record; the minted human session is not scope-narrowed in v1.</summary>
    public string? Scope { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>~60s after mint (RFC 6749 §4.1.2 recommends a short lifetime); gates the exchange.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set the first (and only) time the code is exchanged; a second attempt finds it consumed and is refused.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }
}

/// <summary>
/// A newly minted authorization code. The plaintext exists only in this value,
/// once — it is placed in the 302 <c>code</c> parameter and never persisted.
/// </summary>
public sealed record IssuedAuthorizationCode(string Code, DateTimeOffset ExpiresAt);

/// <summary>
/// Outcome of exchanging an authorization code at the token endpoint. The failure
/// reasons map onto RFC 6749 §5.2 token-error codes at the HTTP boundary; the
/// service names the reason, the endpoint chooses the wire code and status.
/// </summary>
public abstract record CodeExchangeResult
{
    private CodeExchangeResult() { }

    /// <summary>
    /// The code was valid and is now consumed. Carries the scope it was issued
    /// with (for the record) — every other bound field has already been verified.
    /// </summary>
    public sealed record Exchanged(string? Scope) : CodeExchangeResult;

    /// <summary>
    /// The code is unknown, expired, already used, or its bound
    /// client_id/redirect_uri/PKCE/resource did not match this request — all of
    /// which are RFC 6749 §5.2 <c>invalid_grant</c>. One reason string, no detail
    /// that would help an attacker distinguish the cases.
    /// </summary>
    public sealed record InvalidGrant(string Reason) : CodeExchangeResult;
}
