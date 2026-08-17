using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Landbridge.Runner;

/// <summary>
/// landbridged's call into the plane's §11 permission bridge. ACP
/// <c>session/request_permission</c> has no MCP channel of its own — the agent
/// asked the client — so this posts the worker bearer at
/// <c>POST /worker/permission</c> and waits for the Lead or human verdict.
/// </summary>
public static class PlanePermissionClient
{
    private static readonly HttpClient Http = CreateClient();

    /// <summary>
    /// Asks the plane and maps the verdict. A missing server, a transport
    /// failure, or a non-allow response is a deny — the agent must be answered
    /// either way, and auto-allow is how this used to skip the plane entirely.
    /// </summary>
    public static async Task<AcpPermissionDecision> AskAsync(
        IReadOnlyList<AcpMcpServer> servers, AcpPermissionAsk ask, CancellationToken ct)
    {
        var server = servers.Count > 0 ? servers[0] : null;
        if (server is null)
            return new AcpPermissionDecision(false, "no plane MCP server on this dispatch");

        var url = server.Url.TrimEnd('/') + "/worker/permission";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        foreach (var (name, value) in server.Headers)
            req.Headers.TryAddWithoutValidation(name, value);

        var body = new JsonObject
        {
            ["tool"] = ask.Tool,
            ["input"] = ask.InputJson,
        };
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return new AcpPermissionDecision(false, $"plane returned {(int)resp.StatusCode}");

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        var root = doc.RootElement;
        var verdict = root.TryGetProperty("verdict", out var v) ? v.GetString() : null;
        var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
        return new AcpPermissionDecision(
            string.Equals(verdict, "allow", StringComparison.OrdinalIgnoreCase),
            message);
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        return http;
    }
}
