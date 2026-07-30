using System.Text.RegularExpressions;

namespace Docket.Verifier;

/// <summary>
/// The validated <c>docket-verifier</c> configuration (spec §5, §10). Mirrors
/// <c>docketd</c>'s config discipline: argv for non-secret knobs, environment
/// for the secret, and a single parse that reports <b>every</b> problem at once
/// with a clear usage line — no half-configured process ever reaches the loop.
///
/// <para>The verifier bearer credential (§5) is read from the
/// <c>DOCKET_VERIFIER_TOKEN</c> environment variable and <b>never</b> from a
/// flag: argv is visible to every other process on the host (<c>ps</c>,
/// <c>/proc/&lt;pid&gt;/cmdline</c>), so a token on the command line leaks.</para>
///
/// <para>The <c>--accept-pattern</c> is the verifier's <b>own</b> policy (§7):
/// the plane never interprets a task's content, so what counts as an acceptable
/// result reference is entirely the verifier's call. It compiles to a
/// <see cref="Regex"/> once here, with a match timeout so a pathological pattern
/// against a worker-reported reference can never wedge the loop.</para>
/// </summary>
public sealed record VerifierConfig(
    Uri PlaneUrl,
    TimeSpan Interval,
    Regex AcceptPattern,
    string AcceptPatternSource,
    string Token)
{
    /// <summary>The secret is env-only; see the type remarks for why never argv.</summary>
    public const string TokenEnvVar = "DOCKET_VERIFIER_TOKEN";
    public const int DefaultIntervalSeconds = 15;
    public const string DefaultAcceptPattern = ".*";

    /// <summary>A pathological pattern can never wedge a tick (ReDoS guard).</summary>
    public static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromSeconds(1);

    public const string Usage =
        "usage: docket-verifier --plane <url> [--interval <seconds>] [--accept-pattern <regex>]\n" +
        "  --plane           control plane base URL to poll (required)\n" +
        "  --interval        seconds between polls (default 15)\n" +
        "  --accept-pattern  regex a result reference must match to be accepted (default '.*')\n" +
        "  the verifier bearer token is read from $" + TokenEnvVar +
        " (env, never a flag — argv leaks in process lists)";

    /// <summary>Parses and validates; throws <see cref="VerifierConfigException"/> listing every problem.</summary>
    public static VerifierConfig Parse(string[] args, Func<string, string?> getEnv)
    {
        if (!TryParse(args, getEnv, out var config, out var errors))
            throw new VerifierConfigException(errors);
        return config;
    }

    /// <summary>Non-throwing variant; returns every validation failure instead.</summary>
    public static bool TryParse(
        string[] args, Func<string, string?> getEnv,
        out VerifierConfig config, out IReadOnlyList<string> errors)
    {
        config = null!;
        var problems = new List<string>();

        // --plane: required, absolute http/https.
        Uri? plane = null;
        var planeRaw = ArgValue(args, "--plane");
        if (string.IsNullOrWhiteSpace(planeRaw))
            problems.Add("--plane <url> is required — the control plane base URL to poll");
        else if (!Uri.TryCreate(planeRaw, UriKind.Absolute, out plane) ||
                 (plane.Scheme != Uri.UriSchemeHttp && plane.Scheme != Uri.UriSchemeHttps))
            problems.Add($"--plane '{planeRaw}' is not an absolute http/https URL");

        // --interval: optional positive whole seconds; a zero/negative interval
        // would busy-loop the plane, which the "same interval, no backoff storm"
        // contract forbids.
        var interval = TimeSpan.FromSeconds(DefaultIntervalSeconds);
        var intervalRaw = ArgValue(args, "--interval");
        if (intervalRaw is not null)
        {
            if (!int.TryParse(intervalRaw, out var seconds) || seconds < 1)
                problems.Add($"--interval '{intervalRaw}' must be a positive whole number of seconds");
            else
                interval = TimeSpan.FromSeconds(seconds);
        }

        // --accept-pattern: optional; must be a valid regex. RegexOptions.Compiled
        // is deliberately NOT used — it emits IL and would break AOT/trim.
        var patternRaw = ArgValue(args, "--accept-pattern") ?? DefaultAcceptPattern;
        Regex? pattern = null;
        try
        {
            pattern = new Regex(patternRaw, RegexOptions.CultureInvariant, RegexMatchTimeout);
        }
        catch (ArgumentException e)
        {
            problems.Add($"--accept-pattern '{patternRaw}' is not a valid regex: {e.Message}");
        }

        // Secret: env only.
        var token = getEnv(TokenEnvVar);
        if (string.IsNullOrWhiteSpace(token))
            problems.Add($"${TokenEnvVar} is required — the verifier bearer credential (env, never a flag)");

        if (problems.Count > 0)
        {
            errors = problems;
            return false;
        }

        config = new VerifierConfig(plane!, interval, pattern!, patternRaw, token!);
        errors = [];
        return true;
    }

    private static string? ArgValue(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}

/// <summary>Thrown by <see cref="VerifierConfig.Parse"/> with every validation failure.</summary>
public sealed class VerifierConfigException(IReadOnlyList<string> errors)
    : Exception("invalid docket-verifier config:\n  - " + string.Join("\n  - ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
