using System.Net;
using System.Text;
using Docket.Core;

namespace Docket.Mcp.Dashboard;

/// <summary>
/// The dashboard's tiny server-side rendering kit — no template engine, no client
/// framework (§12: "a plain web dashboard first", thin ethos). A handful of pure
/// string helpers: HTML-escape everything dynamic, format ages against the
/// injected <see cref="TimeProvider"/> ("12s ago"), render state badges, and wrap a
/// page body in the shared chrome (nav + the one stylesheet + a 5s meta-refresh).
/// </summary>
internal static class DashboardHtml
{
    /// <summary>Where the single stylesheet is served (see <c>DashboardCss</c>).</summary>
    public const string CssPath = "/dashboard/dashboard.css";

    /// <summary>HTML-encodes a value. Everything user- or wire-derived goes through here.</summary>
    public static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

    public static string E(Guid value) => value.ToString();

    /// <summary>A short id form for dense tables — first segment of a Guid.</summary>
    public static string ShortId(Guid value)
    {
        var s = value.ToString();
        var dash = s.IndexOf('-');
        return dash > 0 ? s[..dash] : s;
    }

    /// <summary>A compact relative age like "12s ago" / "3m ago" / "2h ago" / "5d ago".</summary>
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

    /// <summary>A colored state pill; the CSS class is the lower-cased state name.</summary>
    public static string StateBadge(TaskState state) =>
        $"<span class=\"badge state-{state.ToString().ToLowerInvariant()}\">{E(state.ToString())}</span>";

    public static string Badge(string text, string cssClass) =>
        $"<span class=\"badge {cssClass}\">{E(text)}</span>";

    /// <summary>A labelled slot for a datum §12 lists but the schema does not track yet.</summary>
    public static string NotTracked(string label) =>
        $"<span class=\"nt\" title=\"not tracked yet\">{E(label)}: n/a</span>";

    /// <summary>Wraps a page body in the shared chrome. <paramref name="active"/> keys the nav
    /// highlight; <paramref name="autoRefresh"/> emits the 5s meta-refresh on data pages.</summary>
    public static string Page(string title, string active, string body, bool autoRefresh = true)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        if (autoRefresh)
            sb.Append("<meta http-equiv=\"refresh\" content=\"5\">");
        sb.Append($"<title>{E(title)} · Docket</title>");
        sb.Append($"<link rel=\"stylesheet\" href=\"{CssPath}\">");
        sb.Append("</head><body>");
        sb.Append(Nav(active));
        sb.Append("<main>");
        sb.Append(body);
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static string Nav(string active)
    {
        string Link(string key, string href, string label) =>
            $"<a class=\"{(key == active ? "active" : "")}\" href=\"{href}\">{E(label)}</a>";

        return "<nav><span class=\"brand\">Docket</span>" +
               Link("machines", "/dashboard/machines", "Machines") +
               Link("teams", "/dashboard/teams", "Teams") +
               Link("inbox", "/dashboard/inbox", "Inbox") +
               Link("events", "/dashboard/events", "Events") +
               Link("conformance", "/dashboard/conformance", "Profile check") +
               "<form class=\"logout\" method=\"post\" action=\"/dashboard/logout\">" +
               "<button type=\"submit\">Sign out</button></form>" +
               "</nav>";
    }

    /// <summary>Renders an "everything's clear" empty state inside a section.</summary>
    public static string Empty(string message) => $"<p class=\"empty\">{E(message)}</p>";
}
