using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Docket.Meta.Edge;

/// <summary>
/// The real <see cref="ICaddyAdmin"/> over Caddy's admin API (pure HTTP, design note
/// §4). Routes are addressed by a stable <c>@id</c>: upsert is GET-then-PATCH (when
/// present) or POST-append (when absent), and remove is DELETE-ignore-404. Meta
/// assumes the operator has established the base config — an HTTP server with an
/// (initially empty) <c>routes</c> array and Caddy's automatic HTTPS — as a
/// prerequisite (docs/META.md); meta only manages route entries within it.
/// </summary>
public sealed class CaddyAdmin(HttpClient http, IOptions<MetaOptions> options) : ICaddyAdmin
{
    private readonly string _admin = options.Value.CaddyAdminUrl.TrimEnd('/');
    private readonly string _server = options.Value.CaddyServerName;

    public async Task PingAsync(CancellationToken ct)
    {
        using var resp = await http.GetAsync($"{_admin}/config/", ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<bool> HasRouteAsync(string routeId, CancellationToken ct)
    {
        using var resp = await http.GetAsync($"{_admin}/id/{routeId}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return false;
        resp.EnsureSuccessStatusCode();
        return true;
    }

    public async Task UpsertRouteAsync(string routeId, string host, string upstream, CancellationToken ct)
    {
        var route = BuildRoute(routeId, host, upstream);

        if (await HasRouteAsync(routeId, ct))
        {
            // Replace the existing config node at this @id in place.
            using var patch = await http.PatchAsync($"{_admin}/id/{routeId}", JsonContent.Create(route), ct);
            patch.EnsureSuccessStatusCode();
            return;
        }

        // Append a new route to the server's routes array. The @id embedded in the
        // body registers the handle for later PATCH/DELETE by id.
        using var post = await http.PostAsync(
            $"{_admin}/config/apps/http/servers/{_server}/routes",
            JsonContent.Create(route), ct);
        post.EnsureSuccessStatusCode();
    }

    public async Task RemoveRouteAsync(string routeId, CancellationToken ct)
    {
        using var resp = await http.DeleteAsync($"{_admin}/id/{routeId}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return;
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// A Caddy route object: match the host, reverse_proxy to the upstream. Built with
    /// the DOM so the <c>@id</c> key (not a valid C# identifier) is expressed exactly.
    /// </summary>
    private static JsonObject BuildRoute(string routeId, string host, string upstream) => new()
    {
        ["@id"] = routeId,
        ["match"] = new JsonArray { new JsonObject { ["host"] = new JsonArray { host } } },
        ["handle"] = new JsonArray
        {
            new JsonObject
            {
                ["handler"] = "reverse_proxy",
                ["upstreams"] = new JsonArray { new JsonObject { ["dial"] = upstream } },
            },
        },
        ["terminal"] = true,
    };
}
