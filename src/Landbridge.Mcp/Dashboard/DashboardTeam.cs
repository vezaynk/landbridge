using Microsoft.AspNetCore.WebUtilities;

namespace Landbridge.Mcp.Dashboard;

/// <summary>
/// The operator-chosen Team on the fleet board. Cookie-backed; <c>?team=</c>
/// sets the cookie and the current view. The board shows that Team's sessions only.
/// </summary>
internal static class DashboardTeam
{
    public const string CookieName = "landbridge_team";
    public const string QueryName = "team";

    public static string? ResolveKey(HttpContext http)
    {
        var query = http.Request.Query[QueryName].ToString();
        if (!string.IsNullOrEmpty(query))
            return query;
        var cookie = http.Request.Cookies[CookieName];
        return string.IsNullOrEmpty(cookie) ? null : cookie;
    }

    /// <summary>The <c>team</c> query on a navigation URI, or null when absent.</summary>
    public static string? QueryValue(string uri)
    {
        var parsed = QueryHelpers.ParseQuery(new Uri(uri).Query);
        return parsed.TryGetValue(QueryName, out var values) ? values.ToString() : null;
    }

    public static void WriteCookie(HttpContext http, string key)
    {
        if (http.Response.HasStarted)
            return;
        http.Response.Cookies.Append(CookieName, key, DashboardWindow.PrefCookie(http.Request.IsHttps));
    }

    public static string Href(string currentUri, string key)
    {
        var uri = new Uri(currentUri);
        var parsed = QueryHelpers.ParseQuery(uri.Query);
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in parsed)
        {
            if (kv.Key.Equals(QueryName, StringComparison.OrdinalIgnoreCase))
                continue;
            dict[kv.Key] = kv.Value.ToString();
        }
        dict[QueryName] = key;
        return QueryHelpers.AddQueryString(uri.AbsolutePath, dict);
    }

    public static string Address(Guid id, string? slug) =>
        string.IsNullOrEmpty(slug) ? id.ToString() : slug;
}

/// <summary>Circuit-scoped Team choice so enhanced navigation does not fall back to a stale cookie.</summary>
internal sealed class DashboardTeamState
{
    public string? Key { get; private set; }
    public bool Chosen { get; private set; }

    public void Set(string key)
    {
        Key = key;
        Chosen = true;
    }
}
