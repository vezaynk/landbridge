using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Landbridge.ControlPlane.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// The authorization server on its own host (spec §5). RFC 9728 §3 puts the
/// protected-resource document on the resource server and RFC 8414 §3 puts the
/// authorization-server document at the issuer, so with the two split each host
/// serves exactly one of them and points at the other.
///
/// <para>The property worth guarding is that the chain closes: a 401 from the
/// resource server names a metadata URL, that URL resolves on the host that
/// answered, and the document it returns names the host that can actually mint a
/// token. A split that leaves any link dangling is invisible until a client tries
/// to authenticate — it looks like a working server that nobody can log in to.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OAuthSplitHostTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Each_host_serves_its_own_document_and_points_at_the_other()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var authUrl = $"http://127.0.0.1:{OAuthTestKit.FreePort()}";
        await using var plane = OAuthTestKit.BuildPlane(
            pg.ConnectionString, OAuthTestKit.FreePort(), configureOperator: true, authUrl: authUrl);
        await plane.StartAsync(ct);
        var resourceUrl = OAuthTestKit.BaseUrl(plane);

        await using var auth = OAuthTestKit.BuildAuthServer(
            pg.ConnectionString, new Uri(authUrl).Port, resourceUrl);
        await auth.StartAsync(ct);

        using var http = new HttpClient();

        // ── The resource server's half ────────────────────────────────────────
        var prm = await http.GetFromJsonAsync<JsonElement>(
            $"{resourceUrl}/.well-known/oauth-protected-resource", ct);
        Assert.Equal(resourceUrl, prm.GetProperty("resource").GetString());
        Assert.Equal(authUrl, prm.GetProperty("authorization_servers")[0].GetString());

        // It does not answer for an issuer it is not. A client that fetched RFC 8414
        // metadata from here would be reading a document whose issuer is a different
        // origin, which the draft MCP spec requires it to reject.
        using var asmOnResource = await http.GetAsync(
            $"{resourceUrl}/.well-known/oauth-authorization-server", ct);
        Assert.Equal(HttpStatusCode.NotFound, asmOnResource.StatusCode);

        // ── The authorization server's half ───────────────────────────────────
        var asm = await http.GetFromJsonAsync<JsonElement>(
            $"{authUrl}/.well-known/oauth-authorization-server", ct);
        Assert.Equal(authUrl, asm.GetProperty("issuer").GetString());
        Assert.Equal($"{authUrl}/oauth/authorize", asm.GetProperty("authorization_endpoint").GetString());
        Assert.Equal($"{authUrl}/oauth/token", asm.GetProperty("token_endpoint").GetString());

        using var prmOnAuth = await http.GetAsync(
            $"{authUrl}/.well-known/oauth-protected-resource", ct);
        Assert.Equal(HttpStatusCode.NotFound, prmOnAuth.StatusCode);
    }

    /// <summary>
    /// The 401 challenge must name a document that exists on the host that issued the
    /// challenge. Pointing it at the issuer instead — or leaving it to be derived from
    /// the request on a host that maps no metadata — is how discovery dead-ends on a
    /// 404 that no client reports usefully.
    /// </summary>
    [SkippableFact]
    public async Task The_challenge_names_the_resources_own_metadata_and_that_url_resolves()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var authUrl = $"http://127.0.0.1:{OAuthTestKit.FreePort()}";
        await using var plane = OAuthTestKit.BuildPlane(
            pg.ConnectionString, OAuthTestKit.FreePort(), configureOperator: true, authUrl: authUrl);
        await plane.StartAsync(ct);
        var resourceUrl = OAuthTestKit.BaseUrl(plane);

        using var http = new HttpClient();
        using var unauthorized = await http.PostAsync(
            $"{resourceUrl}/", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), ct);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        var challenge = Assert.Single(unauthorized.Headers.WwwAuthenticate);
        Assert.Equal("Bearer", challenge.Scheme);

        var advertised = ExtractParam(challenge.Parameter!, "resource_metadata");
        Assert.Equal($"{resourceUrl}/.well-known/oauth-protected-resource", advertised);

        // The whole point: follow it and it is there, on this host, naming the other one.
        var document = await http.GetFromJsonAsync<JsonElement>(advertised, ct);
        Assert.Equal(authUrl, document.GetProperty("authorization_servers")[0].GetString());
    }

    /// <summary>Pulls a quoted parameter out of a WWW-Authenticate value.</summary>
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
