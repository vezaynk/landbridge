namespace Landbridge.ControlPlane;

/// <summary>
/// Aspire-loop machine and profile names. Seeded boxes use
/// <c>&lt;harness&gt;-apphost-linux</c>. They run in Linux containers, so the
/// OS is always <c>linux</c> and the hostname is the stable container name
/// <c>apphost</c> — not the plane process's host OS.
/// AppHost and the plane's DevSeed list stay in lockstep by calling here.
/// </summary>
public static class DevSeedNaming
{
    public const string Os = "linux";

    public const string Host = "apphost";

    public static string Box(string harness) => $"{harness}-{Host}-{Os}";

    public static string Profile(string harness) => Box(harness);

    public const string Group = "any-linux";
}
