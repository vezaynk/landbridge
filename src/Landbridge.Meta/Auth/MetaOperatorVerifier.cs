using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;

namespace Landbridge.Meta.Auth;

/// <summary>
/// Verifies the single meta operator's passphrase (spec §3: landbridge-meta is
/// "human-only", a separate credential class from the plane's §5 identities). This
/// is a deliberate, self-contained copy of the plane's
/// <c>ConfiguredOperatorVerifier</c> pattern — meta shares no code and no token
/// store with the plane — down to the same fail-closed properties: an Identity
/// PBKDF2 hash of the passphrase lives in configuration
/// (<c>Meta:Operator:PassphraseHash</c>), never the plaintext, and an unset/garbage
/// or leftover SHA-256 hex value reads as "not configured" so the login door fails
/// closed (503) rather than authenticating anyone.
/// </summary>
public sealed class MetaOperatorVerifier
{
    /// <summary>Config key holding the Identity password hash of the operator passphrase (never the plaintext).</summary>
    public const string PassphraseHashKey = "Meta:Operator:PassphraseHash";

    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object User = new();
    private readonly string? _hash;

    /// <summary>DI entry point: reads the configured passphrase hash.</summary>
    public MetaOperatorVerifier(IConfiguration config) : this(config[PassphraseHashKey]) { }

    /// <summary>Core constructor over the stored hash; kept public so tests skip the config system.</summary>
    public MetaOperatorVerifier(string? passphraseHash)
    {
        _hash = LooksConfigured(passphraseHash) ? passphraseHash!.Trim() : null;
    }

    /// <summary>True iff an operator passphrase is configured at all; when false the login door is fail-closed.</summary>
    public bool IsConfigured => _hash is not null;

    /// <summary>Verify against the configured hash; false whenever unconfigured or blank.</summary>
    public bool Verify(string? passphrase)
    {
        if (_hash is null || string.IsNullOrEmpty(passphrase))
            return false;
        var result = Hasher.VerifyHashedPassword(User, _hash, passphrase);
        return result is PasswordVerificationResult.Success
            or PasswordVerificationResult.SuccessRehashNeeded;
    }

    private static bool LooksConfigured(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return false;
        stored = stored.Trim();
        if (stored.Length == 64 && IsHex(stored))
            return false;
        try
        {
            var bytes = Convert.FromBase64String(stored);
            return bytes.Length > 13 && bytes[0] is 0x00 or 0x01;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
        {
            var hex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex)
                return false;
        }
        return true;
    }
}
