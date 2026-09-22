using Microsoft.AspNetCore.Http;

namespace Landbridge.Mcp;

/// <summary>
/// RFC 7240 <c>Prefer: respond-async</c>. Default is wait-for-Apply so today's
/// Lead loop is unchanged. The preference returns <c>202</c> with a command id;
/// Hub <c>GET /commands/{id}</c> is the pending view.
/// </summary>
public static class PreferHeader
{
    public const string RespondAsync = "respond-async";

    public static bool WantsRespondAsync(HttpRequest request)
    {
        foreach (var header in request.Headers["Prefer"])
        {
            if (string.IsNullOrEmpty(header))
                continue;
            foreach (var token in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var name = token;
                var semi = token.IndexOf(';');
                if (semi >= 0)
                    name = token[..semi].Trim();
                if (name.Equals(RespondAsync, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    public static void ApplyRespondAsync(HttpResponse response) =>
        response.Headers.Append("Preference-Applied", RespondAsync);
}
