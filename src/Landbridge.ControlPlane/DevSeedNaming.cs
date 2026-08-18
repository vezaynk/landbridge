using System.Text.RegularExpressions;

namespace Landbridge.ControlPlane;

/// <summary>
/// Honest Aspire-loop machine and profile names. Enroll convention is
/// <c>&lt;harness&gt;-&lt;hostname&gt;-&lt;os&gt;</c>; the OS is this process's
/// OS, not a pretend linux fleet. AppHost and the plane's DevSeed list stay
/// in lockstep by calling here.
/// </summary>
public static class DevSeedNaming
{
    public static string Os =>
        OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux";

    public static string Host
    {
        get
        {
            var raw = Environment.MachineName.ToLowerInvariant();
            var cleaned = HostChars.Replace(raw, "-").Trim('-');
            return string.IsNullOrEmpty(cleaned) ? "apphost" : cleaned;
        }
    }

    public static string Box(string harness) => $"{harness}-{Host}-{Os}";

    public static string Profile(string harness) => $"{harness}-apphost-{Os}";

    public static string Group => $"any-{Os}";

    private static readonly Regex HostChars = new("[^a-z0-9]+", RegexOptions.CultureInvariant);
}
