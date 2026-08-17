using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Docket.HarnessBump;

/// <summary>
/// Enough of semver 2.0.0 to answer one question: is the registry's <c>latest</c> newer
/// than what ci.yml pins?
/// </summary>
/// <remarks>
/// The prerelease rules (§11.3/§11.4) are implemented rather than ignored because the
/// three harnesses publish prereleases that a naive comparison gets WRONG in both
/// directions, and both mistakes are expensive:
/// <list type="bullet">
///   <item><c>@openai/codex</c> publishes <c>0.148.0-alpha.12</c>, which is numerically
///   above <c>latest</c> (<c>0.147.0</c>) — a triple-only compare would bump to an alpha.</item>
///   <item><c>opencode-ai</c> publishes <c>0.0.0-dev-&lt;stamp&gt;</c> snapshots, which are
///   the newest thing on the registry by publish time yet rank BELOW <c>1.18.18</c>.</item>
/// </list>
/// The tool only ever reads <c>dist-tags.latest</c>, so neither should reach a comparison —
/// this type is the second line of defence, and <see cref="IsPrerelease"/> is the third
/// (<see cref="BumpPlanner"/> refuses to bump to any prerelease, whatever tag it came from).
/// </remarks>
public readonly record struct SemVer(int Major, int Minor, int Patch, string Prerelease)
{
    /// <summary>True for a version carrying a prerelease suffix — never a bump target.</summary>
    public bool IsPrerelease => Prerelease.Length > 0;

    public static bool TryParse(string? text, [NotNullWhen(true)] out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var span = text.Trim();
        // Build metadata (§10) is ignored entirely for ordering.
        var plus = span.IndexOf('+');
        if (plus >= 0)
            span = span[..plus];

        var prerelease = "";
        var dash = span.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = span[(dash + 1)..];
            span = span[..dash];
            // "1.2.3-" is not a version; an empty prerelease is invalid per §9.
            if (prerelease.Length == 0)
                return false;
        }

        var parts = span.Split('.');
        if (parts.Length != 3)
            return false;
        if (!TryParseNumeric(parts[0], out var major) ||
            !TryParseNumeric(parts[1], out var minor) ||
            !TryParseNumeric(parts[2], out var patch))
            return false;

        version = new SemVer(major, minor, patch, prerelease);
        return true;
    }

    /// <summary>Negative when <paramref name="left"/> precedes <paramref name="right"/>.</summary>
    public static int Compare(SemVer left, SemVer right)
    {
        var c = left.Major.CompareTo(right.Major);
        if (c != 0)
            return c;
        c = left.Minor.CompareTo(right.Minor);
        if (c != 0)
            return c;
        c = left.Patch.CompareTo(right.Patch);
        if (c != 0)
            return c;

        // §11.3: a version with no prerelease outranks one that has it.
        if (!left.IsPrerelease && !right.IsPrerelease)
            return 0;
        if (!left.IsPrerelease)
            return 1;
        if (!right.IsPrerelease)
            return -1;
        return ComparePrerelease(left.Prerelease, right.Prerelease);
    }

    /// <summary>§11.4: dot-separated identifiers, numeric ones ranking below alphanumeric ones.</summary>
    private static int ComparePrerelease(string left, string right)
    {
        var leftIds = left.Split('.');
        var rightIds = right.Split('.');
        for (var i = 0; i < Math.Min(leftIds.Length, rightIds.Length); i++)
        {
            var leftNumeric = TryParseNumeric(leftIds[i], out var leftValue);
            var rightNumeric = TryParseNumeric(rightIds[i], out var rightValue);
            var c = (leftNumeric, rightNumeric) switch
            {
                (true, true) => leftValue.CompareTo(rightValue),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(leftIds[i], rightIds[i]),
            };
            if (c != 0)
                return c;
        }
        // §11.4.4: a larger set of fields outranks its own prefix.
        return leftIds.Length.CompareTo(rightIds.Length);
    }

    /// <summary>
    /// Digits only, and no leading zeros (§2/§9) — <c>int.TryParse</c> alone would accept "+1",
    /// "1e2", surrounding whitespace and "007".
    /// </summary>
    private static bool TryParseNumeric(string text, out int value)
    {
        value = 0;
        if (text.Length == 0 || !text.All(char.IsAsciiDigit))
            return false;
        if (text.Length > 1 && text[0] == '0')
            return false;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    public override string ToString() =>
        IsPrerelease ? $"{Major}.{Minor}.{Patch}-{Prerelease}" : $"{Major}.{Minor}.{Patch}";
}
