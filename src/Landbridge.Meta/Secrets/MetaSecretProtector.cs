using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Docket.Meta.Secrets;

/// <summary>
/// Raised when secret protection is misconfigured or a stored value cannot be
/// decrypted. Never caught to fall back on plaintext — a meta that cannot read its
/// own secrets must fail loudly, because the alternative is injecting a wrong
/// credential into a live container (design note §5, task #79).
/// </summary>
public sealed class MetaSecretKeyException(string message) : Exception(message);

/// <summary>
/// Encrypts the per-instance secrets meta must RETAIN to re-inject on resume and
/// upgrade — the instance database password, the relay shared bearer, and a remote
/// host's mTLS client private key. Meta's own Postgres is a high-value store (spec
/// §3: a separate credential class), so these do not sit there in plaintext.
///
/// <para><b>Mechanism.</b> AES-256-GCM per value under an operator-supplied master
/// key, chosen over ASP.NET DataProtection because rotation here must be
/// <i>completable</i>: DataProtection auto-rolls its key ring and can never retire a
/// key while any row still holds a payload under it, so the ring must be retained
/// forever — and on Linux the ring is itself plaintext XML unless a second key
/// management scheme is nested underneath. An explicit key list plus the rewrap
/// sweep (<see cref="SecretRewrapSweeper"/>) lets an operator add a key, rewrap, drop
/// the old key, and be done.</para>
///
/// <para><b>Envelope.</b> <c>mgcm1.&lt;fingerprint&gt;.&lt;base64url(nonce|ciphertext|tag)&gt;</c>.
/// The fingerprint is derived from the key itself, so a value names the key that
/// sealed it without the operator maintaining key ids: decryption picks the matching
/// configured key, which is what makes a multi-key rotation window work. The purpose
/// label is bound as AES-GCM associated data, so a ciphertext cannot be moved
/// between columns.</para>
///
/// <para><b>Fail-closed.</b> No key configured is a startup failure, not a silent
/// downgrade to plaintext. A value sealed under a key that is no longer configured
/// throws rather than returning garbage.</para>
/// </summary>
public sealed class MetaSecretProtector
{
    /// <summary>Config key holding the ordered key list; element 0 is the primary (seals new values).</summary>
    public const string KeysConfigKey = "Meta:Secrets:Keys";

    /// <summary>Envelope version tag. A future scheme bumps this and keeps reading <c>mgcm1</c>.</summary>
    private const string Version = "mgcm1";

    private const int KeyBytes = 32;   // AES-256
    private const int NonceBytes = 12; // GCM standard nonce
    private const int TagBytes = 16;

    /// <summary>Configured keys by fingerprint; the primary is also held separately.</summary>
    private readonly Dictionary<string, byte[]> _byFingerprint;
    private readonly byte[] _primaryKey;

    /// <summary>Fingerprint of the key new values are sealed under — the rewrap sweep's target.</summary>
    public string PrimaryFingerprint { get; }

    /// <summary>
    /// Identity of the WHOLE configured key set, primary first. EF's model cache is
    /// keyed on this rather than on the primary alone: two protectors can share a
    /// primary and differ in their retired keys, and a model cached under the first
    /// would leave the second unable to read rows sealed by a key only it knows about.
    /// </summary>
    public string KeySetId { get; }

    /// <summary>DI entry point: reads <see cref="KeysConfigKey"/>. Throws when unconfigured (fail-closed).</summary>
    public MetaSecretProtector(IConfiguration config)
        : this(config.GetSection(KeysConfigKey).Get<string[]>() ?? []) { }

    /// <summary>Core constructor over raw base64 keys; kept public so tests skip the config system.</summary>
    public MetaSecretProtector(IReadOnlyList<string> base64Keys)
    {
        var keys = new List<byte[]>();
        foreach (var raw in base64Keys)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(raw.Trim()); }
            catch (FormatException)
            {
                throw new MetaSecretKeyException(
                    $"{KeysConfigKey}: not valid base64. Generate one with: openssl rand -base64 32");
            }
            if (bytes.Length != KeyBytes)
                throw new MetaSecretKeyException(
                    $"{KeysConfigKey}: keys must be {KeyBytes} bytes (got {bytes.Length}). Generate one with: openssl rand -base64 32");
            keys.Add(bytes);
        }

        if (keys.Count == 0)
            throw new MetaSecretKeyException(
                $"{KeysConfigKey} is not configured. docket-meta refuses to start without it: the instance " +
                "database passwords, relay bearers, and host mTLS client keys it retains would otherwise be " +
                "written to its Postgres in plaintext. Generate a key with: openssl rand -base64 32 " +
                "(see docs/META.md, Key management).");

        _primaryKey = keys[0];
        PrimaryFingerprint = Fingerprint(_primaryKey);
        _byFingerprint = [];
        foreach (var k in keys)
            _byFingerprint[Fingerprint(k)] = k;
        KeySetId = string.Join(',', keys.Select(Fingerprint));
    }

    /// <summary>True when <paramref name="stored"/> carries this scheme's envelope (vs legacy plaintext).</summary>
    public static bool IsProtected(string? stored) =>
        stored is not null && stored.StartsWith(Version + ".", StringComparison.Ordinal);

    /// <summary>True when the value is sealed under the CURRENT primary key — the rewrap sweep's predicate.</summary>
    public bool IsUnderPrimaryKey(string? stored) =>
        IsProtected(stored) && FingerprintOf(stored!) == PrimaryFingerprint;

    /// <summary>
    /// Seals a value under the primary key. Empty stays empty: destroy shreds a
    /// tombstone's secret columns by blanking them, and an encrypted empty string
    /// would obscure that the shred happened.
    /// </summary>
    public string Protect(string plaintext, string purpose)
    {
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;

        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];

        using var gcm = new AesGcm(_primaryKey, TagBytes);
        gcm.Encrypt(nonce, plain, cipher, tag, Purpose(purpose));

        var payload = new byte[NonceBytes + cipher.Length + TagBytes];
        nonce.CopyTo(payload, 0);
        cipher.CopyTo(payload, NonceBytes);
        tag.CopyTo(payload, NonceBytes + cipher.Length);

        return $"{Version}.{PrimaryFingerprint}.{Base64Url.EncodeToString(payload)}";
    }

    /// <summary>
    /// Opens a sealed value. A value without the envelope is returned unchanged —
    /// pre-encryption rows stay readable so the store migrates forward rather than
    /// stranding an instance; <see cref="SecretRewrapSweeper"/> seals them at startup
    /// and logs each one. Anything that IS sealed must decrypt or this throws.
    /// </summary>
    public string Unprotect(string stored, string purpose)
    {
        if (string.IsNullOrEmpty(stored) || !IsProtected(stored))
            return stored;

        var parts = stored.Split('.', 3);
        if (parts.Length != 3)
            throw new MetaSecretKeyException($"malformed protected value for {purpose}: expected 3 envelope segments");

        var fingerprint = parts[1];
        if (!_byFingerprint.TryGetValue(fingerprint, out var key))
            throw new MetaSecretKeyException(
                $"{purpose} is encrypted under key {fingerprint}, which is not in {KeysConfigKey}. " +
                "Add that key back to the list (it may be a retired key that is still in use) and run " +
                "the rewrap sweep before removing it — see docs/META.md, Key rotation.");

        byte[] payload;
        try { payload = Base64Url.DecodeFromChars(parts[2]); }
        catch (FormatException)
        {
            throw new MetaSecretKeyException($"malformed protected value for {purpose}: payload is not base64url");
        }
        if (payload.Length < NonceBytes + TagBytes)
            throw new MetaSecretKeyException($"malformed protected value for {purpose}: payload too short");

        var nonce = payload.AsSpan(0, NonceBytes);
        var cipher = payload.AsSpan(NonceBytes, payload.Length - NonceBytes - TagBytes);
        var tag = payload.AsSpan(payload.Length - TagBytes, TagBytes);
        var plain = new byte[cipher.Length];

        try
        {
            using var gcm = new AesGcm(key, TagBytes);
            gcm.Decrypt(nonce, cipher, tag, plain, Purpose(purpose));
        }
        catch (CryptographicException)
        {
            // Authentication failure: the wrong key for this fingerprint, a tampered
            // row, or a value moved between columns (the purpose is bound in).
            throw new MetaSecretKeyException(
                $"{purpose} failed authenticated decryption under key {fingerprint}. The key does not match " +
                "this value, or the stored row was altered. Refusing to continue rather than inject a wrong credential.");
        }

        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>A fresh base64 key, for the docs' generate-a-key step and for tests.</summary>
    public static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeyBytes));

    /// <summary>Short, stable, non-secret name for a key: the head of its SHA-256.</summary>
    private static string Fingerprint(byte[] key) =>
        Convert.ToHexString(SHA256.HashData(key))[..8].ToLowerInvariant();

    private static string? FingerprintOf(string stored)
    {
        var parts = stored.Split('.', 3);
        return parts.Length == 3 ? parts[1] : null;
    }

    private static byte[] Purpose(string purpose) => Encoding.UTF8.GetBytes(purpose);
}
