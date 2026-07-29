using System.Security.Cryptography;
using System.Text;

namespace Docket.ControlPlane.Auth;

/// <summary>
/// Proof Key for Code Exchange (RFC 7636), <b>S256 only</b>. OAuth 2.1 §4.1.1
/// requires PKCE for the authorization-code grant and the MCP authorization spec
/// requires clients to use S256 when technically capable; Docket therefore
/// advertises and accepts <c>S256</c> alone and rejects <c>plain</c> outright, so
/// an authorization code is never bound to a downgradable challenge.
/// </summary>
public static class Pkce
{
    /// <summary>The one code-challenge method Docket supports (RFC 7636 §4.2).</summary>
    public const string S256 = "S256";

    /// <summary>
    /// True iff <paramref name="method"/> is exactly <c>S256</c>. A missing or
    /// <c>plain</c> method is rejected at the authorize endpoint before any code
    /// is minted (OAuth 2.1: no downgrade).
    /// </summary>
    public static bool IsSupportedMethod(string? method) =>
        string.Equals(method, S256, StringComparison.Ordinal);

    /// <summary>
    /// Verifies an RFC 7636 §4.6 S256 exchange: <c>BASE64URL(SHA256(verifier))</c>
    /// must equal the stored <paramref name="codeChallenge"/>. The compare is
    /// constant-time so a mismatched verifier leaks no timing signal about how much
    /// of the challenge matched.
    /// </summary>
    public static bool VerifyS256(string codeVerifier, string codeChallenge)
    {
        if (string.IsNullOrEmpty(codeVerifier) || string.IsNullOrEmpty(codeChallenge))
            return false;

        var computed = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(codeChallenge));
    }

    /// <summary>Base64url without padding (RFC 7636 §A / RFC 4648 §5).</summary>
    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
