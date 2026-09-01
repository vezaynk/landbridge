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
    /// Verifies a presented operator passphrase. Returns false for a wrong
    /// passphrase and (defensively) whenever <see cref="IsConfigured"/> is false —
    /// the endpoint checks configuration first, but this never returns true against
    /// an absent credential.
    /// </summary>
    bool Verify(string? passphrase);
}

/// <summary>
/// The operator verifier: a single shared passphrase whose Identity PBKDF2 hash
/// is held in configuration (<c>Landbridge:Operator:PassphraseHash</c>) — never the
/// plaintext (§5, §13). A leftover SHA-256 hex is treated as unconfigured.
/// </summary>
public sealed class ConfiguredOperatorVerifier : IOperatorVerifier
{
    /// <summary>Config key holding the Identity password hash of the operator passphrase (never the plaintext).</summary>
    public const string PassphraseHashKey = "Landbridge:Operator:PassphraseHash";

    private readonly string? _hash;

    /// <summary>DI entry point: reads the configured passphrase hash (§5).</summary>
    public ConfiguredOperatorVerifier(IConfiguration config) : this(config[PassphraseHashKey]) { }

    /// <summary>
    /// Core constructor over the stored hash (never the plaintext). A blank or
    /// leftover SHA-256 hex is treated as "not configured" so the endpoint fails
    /// closed. Kept public so tests exercise verification without the configuration
    /// system.
    /// </summary>
    public ConfiguredOperatorVerifier(string? passphraseHash)
    {
        _hash = OperatorPassphrase.LooksConfigured(passphraseHash) ? passphraseHash!.Trim() : null;
    }

    public bool IsConfigured => _hash is not null;

    public bool Verify(string? passphrase) =>
        _hash is not null && OperatorPassphrase.Verify(_hash, passphrase);
}
