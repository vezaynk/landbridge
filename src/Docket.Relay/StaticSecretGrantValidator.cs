using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Docket.Relay;

/// <summary>Options for <see cref="StaticSecretGrantValidator"/> (config section <c>Relay:Grant</c>).</summary>
public sealed class StaticSecretGrantValidatorOptions
{
    public const string SectionName = "Relay:Grant";

    /// <summary>
    /// Accept every grant. Loudly insecure — for local dev / smoke only. Real
    /// grant checking is the control-plane validator (a later increment).
    /// </summary>
    public bool AllowAll { get; set; }

    /// <summary>A single shared secret; a grant is accepted iff it equals this.</summary>
    public string? SharedSecret { get; set; }
}

/// <summary>
/// The default, fail-closed <see cref="IGrantValidator"/> for this increment.
/// With no configuration it rejects every grant; set <c>Relay:Grant:AllowAll</c>
/// or <c>Relay:Grant:SharedSecret</c> to open it. This is a placeholder for the
/// real control-plane grant check (spec §8.3), which is a later increment — no
/// control-plane URL is baked in here.
/// </summary>
public sealed class StaticSecretGrantValidator : IGrantValidator
{
    private readonly StaticSecretGrantValidatorOptions _options;

    public StaticSecretGrantValidator(
        IOptions<StaticSecretGrantValidatorOptions> options,
        ILogger<StaticSecretGrantValidator> logger)
    {
        _options = options.Value;

        if (_options.AllowAll)
            logger.LogWarning(
                "Relay grant validation is set to AllowAll — every grant is accepted. Do not run this way outside local dev.");
        else if (string.IsNullOrEmpty(_options.SharedSecret))
            logger.LogWarning(
                "Relay has no grant validator configured (no SharedSecret, AllowAll off) — all tunnels will be refused until the control-plane validator is wired in.");
    }

    public Task<bool> ValidateAsync(string grant, string forwardId, RelayRole role, CancellationToken ct)
    {
        if (_options.AllowAll)
            return Task.FromResult(true);

        if (string.IsNullOrEmpty(_options.SharedSecret))
            return Task.FromResult(false);

        // Constant-time compare so the relay doesn't leak the secret's length or
        // a match prefix through timing. FixedTimeEquals returns false for a
        // length mismatch without short-circuiting.
        var expected = Encoding.UTF8.GetBytes(_options.SharedSecret);
        var actual = Encoding.UTF8.GetBytes(grant);
        return Task.FromResult(CryptographicOperations.FixedTimeEquals(expected, actual));
    }
}
