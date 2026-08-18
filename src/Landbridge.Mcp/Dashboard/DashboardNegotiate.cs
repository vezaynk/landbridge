using System.Text.Json;
using System.Text.Json.Serialization;

namespace Landbridge.Mcp.Dashboard;

/// <summary>
/// Shared content negotiation for the §12 dashboard. JSON is requested by
/// <c>?format=json</c> or by an <c>Accept: application/json</c> that does not
/// also accept HTML. A browser always gets the Blazor page unless it asks.
/// </summary>
internal static class DashboardNegotiate
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
    };

    public static bool WantsJson(HttpContext http)
    {
        var format = http.Request.Query["format"].ToString();
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(format, "html", StringComparison.OrdinalIgnoreCase))
            return false;
        var accept = http.Request.Headers.Accept.ToString();
        return accept.Contains("application/json", StringComparison.OrdinalIgnoreCase)
            && !accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }
}
