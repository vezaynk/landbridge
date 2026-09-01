using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;

namespace Landbridge.Meta.Provisioning;

/// <summary>
/// Mints an Instance's fresh per-instance secrets (design note §5). Hex values are
/// safe to drop into a connection string, an HTTP header, and a shell-free env map
/// without escaping. The operator passphrase is generated, returned once to the
/// caller for the shown-once page, and immediately reduced to an Identity PBKDF2
/// hash — the plaintext is never persisted (mirrors the plane's
/// <c>Landbridge:Operator:PassphraseHash</c> discipline, §5/§13).
/// </summary>
public class SecretGenerator
{
    /// <summary>A high-entropy hex secret for internal use (DB password, shared relay bearer).</summary>
    public virtual string NewSecret() => RandomNumberGenerator.GetHexString(48, lowercase: true);

    /// <summary>A high-entropy operator passphrase, shown to the operator exactly once at create.</summary>
    public virtual string NewPassphrase() => RandomNumberGenerator.GetHexString(32, lowercase: true);

    /// <summary>Identity PBKDF2 hash of a passphrase — the form injected into the instance and stored at rest.</summary>
    public static string Hash(string passphrase) =>
        new PasswordHasher<object>().HashPassword(new object(), passphrase);
}
