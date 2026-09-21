using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
using Landbridge.Mcp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// A host built the way the split hosts are built — <c>AddPlane</c> plus a bearer-gated
/// endpoint, and nothing OAuth-specific wired by hand.
///
/// <para>The property is that such a host is a coherent resource server on its own: its
/// 401 names a resource-metadata document, and that document is on the host that issued
/// the challenge. Before <c>AddPlane</c> registered an <see cref="OAuthServerConfig"/>,
/// a host like this challenged with a URL derived from the request and served nothing
/// there — a 401 pointing at a 404, which no part of the exchange reports as wrong.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SplitHostDiscoveryTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private const string Resource = "https://mcp.example.com";
    private const string Issuer = "https://auth.example.com";

    [SkippableFact]
    public async Task A_split_host_challenge_names_a_document_that_host_serves()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var host = BuildSplitHost();
        await host.StartAsync(ct);
        var baseUrl = OAuthTestKit.BaseUrl(host);

        using var http = new HttpClient();
        using var unauthorized = await http.GetAsync($"{baseUrl}/gated", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var challenge = Assert.Single(unauthorized.Headers.WwwAuthenticate);
        var advertised = ExtractParam(challenge.Parameter!, "resource_metadata");

        // Configured, not guessed from the request host — and reachable here.
        Assert.Equal($"{Resource}/.well-known/oauth-protected-resource", advertised);

        var document = await http.GetFromJsonAsync<JsonElement>(
            $"{baseUrl}/.well-known/oauth-protected-resource", ct);
        Assert.Equal(Resource, document.GetProperty("resource").GetString());
        Assert.Equal(Issuer, document.GetProperty("authorization_servers")[0].GetString());
    }

    /// <summary>
    /// The authorization server's document is not this host's to serve. Answering it here
    /// would hand a client RFC 8414 metadata whose issuer is a different origin, which the
    /// draft MCP spec requires it to reject.
    /// </summary>
    [SkippableFact]
    public async Task A_split_host_does_not_answer_for_the_authorization_server()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var host = BuildSplitHost();
        await host.StartAsync(ct);

        using var http = new HttpClient();
        using var asm = await http.GetAsync(
            $"{OAuthTestKit.BaseUrl(host)}/.well-known/oauth-authorization-server", ct);

        Assert.Equal(HttpStatusCode.NotFound, asm.StatusCode);
    }

    private WebApplication BuildSplitHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{OAuthTestKit.FreePort()}");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Landbridge"] = pg.ConnectionString,
            ["Landbridge:PublicMcpUrl"] = Resource,
            ["Landbridge:AuthUrl"] = Issuer,
        });

        builder.AddPlane();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOAuthResourceMetadata();
        app.MapGet("/gated", () => Microsoft.AspNetCore.Http.Results.Ok()).RequireAuthorization();
        return app;
    }

    private static string ExtractParam(string parameter, string name)
    {
        foreach (var part in parameter.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith($"{name}=", StringComparison.Ordinal))
                return trimmed[(name.Length + 1)..].Trim('"');
        }

        throw new InvalidOperationException($"'{name}' not found in WWW-Authenticate: {parameter}");
    }
}
