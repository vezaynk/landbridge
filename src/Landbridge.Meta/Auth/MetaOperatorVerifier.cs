using System.Security.Cryptography;
using System.Text;

namespace Landbridge.Meta.Auth;

/// <summary>
/// Verifies the single meta operator's passphrase (spec §3: landbridge-meta is
/// "human-only", a separate credential class from the plane's §5 identities). This
/// is a deliberate, self-contained copy of the plane's
/// <c>ConfiguredOperatorVerifier</c> pattern — meta shares no code and no token
/// store with the plane — down to the same fail-closed and constant-time
/// properties: the SHA-256 hex of the passphrase lives in configuration
/// (<c>Meta:Operator:PassphraseHash</c>), never the plaintext, and an unset/garbage
/// value reads as "not configured" so the login door fails closed (503) rather
/// than authenticating anyone.
/// </summary>
public sealed class MetaOperatorVerifier
{
    /// <summary>Config key holding the SHA-256 hex of the operator passphrase (never the plaintext).</summary>
    public const string PassphraseHashKey = "Meta:Operator:PassphraseHash";

    private readonly byte[]? _expectedHashBytes;

    /// <summary>DI entry point: reads the configured passphrase hash.</summary>
    public MetaOperatorVerifier(IConfiguration config) : this(config[PassphraseHashKey]) { }

    /// <summary>Core constructor over the raw SHA-256 hex; kept public so tests skip the config system.</summary>
    public MetaOperatorVerifier(string? passphraseHashHex)
    {
        _expectedHashBytes = TryDecodeHex(passphraseHashHex);
    }

    /// <summary>True iff an operator passphrase is configured at all; when false the login door is fail-closed.</summary>
    public bool IsConfigured => _expectedHashBytes is not null;

    /// <summary>Constant-time verify against the configured hash; false whenever unconfigured or blank.</summary>
    public bool Verify(string? passphrase)
    {
        if (_expectedHashBytes is null || string.IsNullOrEmpty(passphrase))
            return false;

        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(passphrase));
        return CryptographicOperations.FixedTimeEquals(presentedHash, _expectedHashBytes);
    }

    private static byte[]? TryDecodeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;
        try
        {
            var bytes = Convert.FromHexString(hex.Trim());
            return bytes.Length == 32 ? bytes : null; // must be a SHA-256 digest
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
