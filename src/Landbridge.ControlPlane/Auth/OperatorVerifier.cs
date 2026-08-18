using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Landbridge.ControlPlane.Auth;

/// <summary>
/// Verifies that the human driving the OAuth authorize step is <em>the
/// operator</em> of this Instance (spec §5: a human session is the root every
/// other credential descends from, §2 principle 5). This interface is the seam a
/// future multi-user / <c>landbridge-meta</c> identity story replaces — today there
/// is one operator per Instance, proven by a shared passphrase, and the whole
/// point is that the wire protocol above it (the OAuth flow) does not change when
/// the identity source does.
/// </summary>
public interface IOperatorVerifier
{
    /// <summary>
    /// True iff this Instance has an operator credential configured at all. When
    /// false the authorize endpoint is <b>fail-closed</b>: it mints nothing and
    /// returns 503, rather than letting an unconfigured server hand out sessions
    /// (mirrors <c>RelayValidationEndpoints</c>' bearer, §8.3/§13).
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Verifies a presented operator passphrase, in constant time. Returns false
    /// for a wrong passphrase and (defensively) whenever <see cref="IsConfigured"/>
    /// is false — the endpoint checks configuration first, but this never returns
    /// true against an absent credential.
    /// </summary>
    bool Verify(string? passphrase);
}

/// <summary>
/// The v1 operator verifier: a single shared passphrase whose SHA-256 hex is held
/// in configuration (<c>Landbridge:Operator:PassphraseHash</c>) — never the plaintext
/// (§5, §13: opaque credentials hashed at rest). Verification hashes the presented
/// passphrase the same way and compares the two <em>hashes</em> in constant time,
/// so both the compare length and the timing are independent of the real secret.
///
/// <para>Real rate-limiting is a follow-up; the authorize endpoint adds a small
/// fixed delay on a failed attempt as cheap brute-force friction. A per-user
/// credential store slots in behind this same interface without touching the
/// endpoints.</para>
/// </summary>
public sealed class ConfiguredOperatorVerifier : IOperatorVerifier
{
    /// <summary>Config key holding the SHA-256 hex of the operator passphrase (never the plaintext).</summary>
    public const string PassphraseHashKey = "Landbridge:Operator:PassphraseHash";

    private readonly byte[]? _expectedHashBytes;

    /// <summary>DI entry point: reads the configured passphrase hash (§5).</summary>
    public ConfiguredOperatorVerifier(IConfiguration config) : this(config[PassphraseHashKey]) { }

    /// <summary>
    /// Core constructor over the raw SHA-256 hex (never the plaintext). A blank or
    /// malformed value is treated as "not configured" so the endpoint fails closed
    /// rather than comparing against garbage. Kept public so tests exercise the
    /// verification without depending on the configuration system.
    /// </summary>
    public ConfiguredOperatorVerifier(string? passphraseHashHex)
    {
        _expectedHashBytes = TryDecodeHex(passphraseHashHex);
    }

    public bool IsConfigured => _expectedHashBytes is not null;

    public bool Verify(string? passphrase)
    {
        if (_expectedHashBytes is null || string.IsNullOrEmpty(passphrase))
            return false;

        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(passphrase));
        // Both operands are fixed 32-byte SHA-256 digests, so the compare's length
        // and timing reveal nothing about the configured secret.
        return CryptographicOperations.FixedTimeEquals(presentedHash, _expectedHashBytes);
    }

    private static byte[]? TryDecodeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;
        try
        {
            var bytes = Convert.FromHexString(hex.Trim());
            // Must be a SHA-256 digest; anything else is a misconfiguration and
            // fails closed.
            return bytes.Length == 32 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
