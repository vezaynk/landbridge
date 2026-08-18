using System.Net;
using System.Text;
using Landbridge.Meta.Data;

namespace Landbridge.Meta.Web;

/// <summary>
/// Meta's tiny server-side render kit — no template engine, no client framework,
/// deliberately mirroring the plane dashboard's ethos (spec §12 "a plain web
/// dashboard first"). Pure string helpers: HTML-escape everything dynamic, format
/// ages, render state badges, wrap a body in the shared chrome. A self-contained
/// copy (meta shares no code with the plane) rather than a dependency.
/// </summary>
internal static class MetaHtml
{
    public const string CssPath = "/meta.css";

    /// <summary>HTML-encodes a value. Everything dynamic goes through here.</summary>
    public static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

    public static string E(Guid value) => value.ToString();

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
        if (d.TotalSeconds < 60) return $"{(int)d.TotalSeconds}s ago";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }

    /// <summary>A colored state pill; when an operation is in flight, that verb shows over the state.</summary>
    public static string StateBadge(InstanceState state, string? busyVerb = null) =>
        busyVerb is not null
            ? $"<span class=\"badge busy\">{E(busyVerb)}</span>"
            : $"<span class=\"badge state-{state.ToString().ToLowerInvariant()}\">{E(state.ToString())}</span>";

    public static string Badge(string text, string cssClass) =>
        $"<span class=\"badge {cssClass}\">{E(text)}</span>";

    public static string Empty(string message) => $"<p class=\"empty\">{E(message)}</p>";

    /// <summary>Wraps a page body in the shared chrome. <paramref name="autoRefresh"/> emits a 5s meta-refresh.</summary>
    public static string Page(string title, string active, string body, bool autoRefresh = true)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        if (autoRefresh)
            sb.Append("<meta http-equiv=\"refresh\" content=\"5\">");
        sb.Append($"<title>{E(title)} · Landbridge Meta</title>");
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

        return "<nav><span class=\"brand\">Landbridge Meta</span>" +
               Link("instances", "/instances", "Instances") +
               Link("hosts", "/hosts", "Hosts") +
               "<form class=\"logout\" method=\"post\" action=\"/logout\">" +
               "<button type=\"submit\">Sign out</button></form>" +
               "</nav>";
    }
}
