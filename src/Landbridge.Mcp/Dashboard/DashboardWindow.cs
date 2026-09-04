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

    /// <summary>The <c>window</c> query on a navigation URI, or null when absent.</summary>
    public static string? QueryValue(string uri)
    {
        var parsed = QueryHelpers.ParseQuery(new Uri(uri).Query);
        return parsed.TryGetValue(QueryName, out var values) ? values.ToString() : null;
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
        http.Response.Cookies.Append(CookieName, key, PrefCookie(http.Request.IsHttps));
    }

    /// <summary>
    /// Lookback is a UI preference, not a credential: HttpOnly is off so the
    /// circuit can keep the cookie in sync after enhanced navigation (the HTTP
    /// response has already started).
    /// </summary>
    public static CookieOptions PrefCookie(bool secure) => new()
    {
        HttpOnly = false,
        SameSite = SameSiteMode.Lax,
        Secure = secure,
        Path = "/dashboard",
        Expires = DateTimeOffset.UtcNow.AddYears(1),
    };

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
    public DashboardWindowOption Current { get; private set; } = DashboardWindow.Default;
    public bool Chosen { get; private set; }

    public void Set(DashboardWindowOption option)
    {
        Current = option;
        Chosen = true;
    }
}
