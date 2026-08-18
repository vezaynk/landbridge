using System.Text.RegularExpressions;

namespace Landbridge.Meta.Provisioning;

/// <summary>
/// Derives every stable name for an Instance from its DNS-safe <c>Name</c> (design
/// note §1/§4): Docker object names, on-network container hostnames, Caddy route
/// ids, and the two public URLs. Deterministic so the saga's adopt-by-name and the
/// destroy's remove-by-name always agree.
/// </summary>
public static partial class InstanceNaming
{
    /// <summary>The container port every ASP.NET host in an instance listens on (set via ASPNETCORE_URLS).</summary>
    public const int ContainerHttpPort = 8080;

    public static string NetworkName(string name) => $"landbridge-{name}-net";
    public static string VolumeName(string name) => $"landbridge-{name}-pgdata";
    public static string PgContainer(string name) => $"landbridge-{name}-pg";
    public static string McpContainer(string name) => $"landbridge-{name}-mcp";
    public static string RelayContainer(string name) => $"landbridge-{name}-relay";

    public static string McpRouteId(string name) => $"landbridge-{name}-mcp";
    public static string RelayRouteId(string name) => $"landbridge-{name}-relay";

    /// <summary>The instance's public MCP host: <c>&lt;name&gt;.landbridge.&lt;domain&gt;</c>.</summary>
    public static string McpHost(string name, string domain) => $"{name}.landbridge.{domain}";

    /// <summary>The instance's public relay host: <c>relay-&lt;name&gt;.landbridge.&lt;domain&gt;</c> (deviation #2).</summary>
    public static string RelayHost(string name, string domain) => $"relay-{name}.landbridge.{domain}";

    public static string McpPublicUrl(string name, string domain) => $"https://{McpHost(name, domain)}";
    public static string RelayPublicUrl(string name, string domain) => $"https://{RelayHost(name, domain)}";

    /// <summary>
    /// A valid instance name is a single DNS label: lowercase alphanumerics and
    /// hyphens, not starting/ending with a hyphen, 1–40 chars. This keeps every
    /// derived Docker name and subdomain well-formed.
    /// </summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 40 && NameRegex().IsMatch(name);

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex NameRegex();
}
