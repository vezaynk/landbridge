using Microsoft.AspNetCore.WebUtilities;

namespace Landbridge.Mcp.Dashboard;

internal sealed record DashboardWindowOption(string Key, string Label, TimeSpan Duration);

/// <summary>
/// The operator-chosen lookback for dashboard event surfaces (fleet marks/tail,
/// the event log, friction). Cookie-backed so it sticks across pages; a
/// <c>?window=</c> query sets the cookie and the current view.
/// </summary>
internal static class DashboardWindow
{
    public const string CookieName = "landbridge_window";
    public const string QueryName = "window";

    public static readonly DashboardWindowOption[] Options =
    [
        new("5m", "5 min", TimeSpan.FromMinutes(5)),
        new("30m", "30 min", TimeSpan.FromMinutes(30)),
        new("2h", "2 hr", TimeSpan.FromHours(2)),
        new("1d", "1 day", TimeSpan.FromDays(1)),
    ];

    public static DashboardWindowOption Default { get; } = Options[1];

    public static DashboardWindowOption Resolve(HttpContext http)
    {
        var query = http.Request.Query[QueryName].ToString();
        if (TryParse(query, out var fromQuery))
            return fromQuery;
        var cookie = http.Request.Cookies[CookieName];
        if (TryParse(cookie, out var fromCookie))
            return fromCookie;
        return Default;
    }

    public static bool TryParse(string? key, out DashboardWindowOption option)
    {
        if (!string.IsNullOrEmpty(key))
        {
            foreach (var o in Options)
            {
                if (o.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    option = o;
                    return true;
                }
            }
        }
        option = Default;
        return false;
    }

    public static void WriteCookie(HttpContext http, string key)
    {
        if (http.Response.HasStarted)
            return;
        http.Response.Cookies.Append(CookieName, key, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
            Path = "/dashboard",
            Expires = DateTimeOffset.UtcNow.AddYears(1),
        });
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
}

/// <summary>Circuit-scoped current window so the footer and pages share one choice.</summary>
internal sealed class DashboardWindowState
{
    public DashboardWindowOption Current { get; set; } = DashboardWindow.Default;
}
