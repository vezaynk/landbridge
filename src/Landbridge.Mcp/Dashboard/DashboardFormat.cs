using System.Globalization;
using System.Net;
using Landbridge.ControlPlane;
using Landbridge.Core;

namespace Landbridge.Mcp.Dashboard;

/// <summary>
/// Pure formatters the Blazor pages share. Escaping is Razor's job; these only
/// shape values (ages, short ids, token counts, byte sizes).
/// </summary>
internal static class DashboardFormat
{
    public static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

    public static string ShortId(Guid value)
    {
        var s = value.ToString();
        var dash = s.IndexOf('-');
        return dash > 0 ? s[..dash] : s;
    }

    public static string Age(DateTimeOffset? ts, DateTimeOffset now)
    {
        if (ts is null)
            return "—";
        var d = now - ts.Value;
        if (d < TimeSpan.Zero)
            d = TimeSpan.Zero;
        if (d.TotalSeconds < 60)
            return $"{(int)d.TotalSeconds}s ago";
        if (d.TotalMinutes < 60)
            return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24)
            return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }

    public static string StateClass(SessionState state) =>
        "state-" + state.ToString().ToLowerInvariant();

    public static string KindText(InputRequestKind? kind) =>
        kind?.ToString().ToLowerInvariant() ?? "kind not recorded";

    public static string ProvenanceLabel(VerdictProvenance p) => p switch
    {
        VerdictProvenance.LeadSession => "lead session",
        VerdictProvenance.Human => "a human",
        _ => p.ToString(),
    };

    public static string Tokens(long count) => count.ToString("N0", CultureInfo.InvariantCulture);

    public static string Usd(decimal amount) => amount.ToString("0.####", CultureInfo.InvariantCulture);

    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes} B"
            : $"{value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    public static string StreamBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):N1} MB",
    };

    public static string CostCell(decimal? cost, UsageCostProvenance provenance) =>
        (cost, provenance) switch
        {
            ({ } c, UsageCostProvenance.Reported) => $"{Usd(c)} USD",
            ({ } c, UsageCostProvenance.Derived) => $"~{Usd(c)} USD est.",
            _ => "not reported",
        };
}
