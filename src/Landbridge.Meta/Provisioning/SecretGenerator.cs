using System.Security.Cryptography;
using System.Text;

namespace Docket.Meta.Provisioning;

/// <summary>
/// Mints an Instance's fresh per-instance secrets (design note §5). All values are
/// hex so they are safe to drop into a connection string, an HTTP header, and a
/// shell-free env map without escaping. The operator passphrase is generated,
/// returned once to the caller for the shown-once page, and immediately reduced to
/// its SHA-256 hash — the plaintext is never persisted (mirrors the plane's
/// <c>Docket:Operator:PassphraseHash</c> discipline, §5/§13).
/// </summary>
public class SecretGenerator
{
    /// <summary>A high-entropy hex secret for internal use (DB password, shared relay bearer).</summary>
    public virtual string NewSecret() => RandomNumberGenerator.GetHexString(48, lowercase: true);

    /// <summary>A high-entropy operator passphrase, shown to the operator exactly once at create.</summary>
    public virtual string NewPassphrase() => RandomNumberGenerator.GetHexString(32, lowercase: true);

    /// <summary>SHA-256 hex of a passphrase — the form injected into the instance and stored at rest.</summary>
    public static string Hash(string passphrase) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(passphrase))).ToLowerInvariant();
}
