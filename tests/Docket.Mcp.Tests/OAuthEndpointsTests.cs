using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Docket.ControlPlane.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Hosting;

namespace Docket.Mcp.Tests;

/// <summary>
/// Endpoint-level behaviour of the OAuth 2.1 surface (spec §5): the discovery
/// documents' shape, the fail-closed authorize endpoint, and the authorize/token
/// error paths — CIMD validation, redirect-URI registration (with the RFC 8252
/// loopback exception), PKCE S256-only, RFC 8707 resource binding, and RFC 6749
/// §5.2 token errors. Each runs the real host against the fixture Postgres.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OAuthEndpointsTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static HttpClient NewClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false });

    // ── Discovery documents ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task Metadata_documents_advertise_the_expected_oauth_surface()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var (plane, baseUrl, ct) = await StartPlaneAsync(configureOperator: true);
        await using var host = plane;
        try
        {
            using var http = NewClient();

            var prm = await http.GetFromJsonAsync<JsonElement>($"{baseUrl}/.well-known/oauth-protected-resource", ct);
            Assert.Equal(baseUrl, prm.GetProperty("resource").GetString());
            Assert.Equal(baseUrl, prm.GetProperty("authorization_servers")[0].GetString());

            var asm = await http.GetFromJsonAsync<JsonElement>($"{baseUrl}/.well-known/oauth-authorization-server", ct);
            Assert.Equal(baseUrl, asm.GetProperty("issuer").GetString());
            Assert.Equal($"{baseUrl}/oauth/authorize", asm.GetProperty("authorization_endpoint").GetString());
            Assert.Equal($"{baseUrl}/oauth/token", asm.GetProperty("token_endpoint").GetString());
            Assert.Equal(["code"], asm.GetProperty("response_types_supported").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal(["authorization_code"], asm.GetProperty("grant_types_supported").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal(["S256"], asm.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal(["none"], asm.GetProperty("token_endpoint_auth_methods_supported").EnumerateArray().Select(e => e.GetString()));
            Assert.True(asm.GetProperty("client_id_metadata_document_supported").GetBoolean());
            // DCR is intentionally absent — no registration_endpoint.
            Assert.False(asm.TryGetProperty("registration_endpoint", out _));
        }
        finally { await plane.StopAsync(ct); }
    }

    // ── Fail-closed ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Authorize_is_fail_closed_when_no_operator_passphrase_is_configured()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var (plane, baseUrl, ct) = await StartPlaneAsync(configureOperator: false);
        await using var host = plane;
        try
        {
            using var http = NewClient();
            // No operator credential → the front door mints nothing (503), on both verbs.
            using var get = await http.GetAsync($"{baseUrl}/oauth/authorize?response_type=code&client_id=x", ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, get.StatusCode);
            using var post = await http.PostAsync($"{baseUrl}/oauth/authorize",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = "x" }), ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, post.StatusCode);
        }
        finally { await plane.StopAsync(ct); }
    }

    // ── Authorize error paths ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task Authorize_rejects_an_unfetchable_client_id_as_invalid_client_without_redirecting()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var (plane, baseUrl, ct) = await StartPlaneAsync(configureOperator: true);
        await using var host = plane;
        try
        {
            using var http = NewClient();
            // A loopback client_id with nothing listening → the CIMD fetch fails.
            var deadClientId = $"http://127.0.0.1:{OAuthTestKit.FreePort()}/client.json";
            var url = QueryHelpers.AddQueryString($"{baseUrl}/oauth/authorize", new Dictionary<string, string?>
            {
                ["response_type"] = "code",
                ["client_id"] = deadClientId,
                ["redirect_uri"] = "http://127.0.0.1:1/callback",
                ["code_challenge"] = OAuthTestKit.S256Challenge(OAuthTestKit.NewCodeVerifier()),
                ["code_challenge_method"] = "S256",
                ["state"] = "s",
            });
            using var resp = await http.GetAsync(url, ct);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Null(resp.Headers.Location); // never redirect on a client_id/redirect problem
            Assert.Contains("invalid_client", await resp.Content.ReadAsStringAsync(ct));
        }
        finally { await plane.StopAsync(ct); }
    }

    [SkippableFact]
    public async Task Authorize_rejects_a_redirect_uri_not_registered_in_the_client_metadata()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var (plane, baseUrl, ct) = await StartPlaneAsync(configureOperator: true);
        await using var host = plane;
        var (cimd, clientId) = OAuthTestKit.BuildCimdServer("Registered Client", "https://app.example.com/callback");
        await cimd.StartAsync(ct);
        try
        {
            using var http = NewClient();
            var url = AuthorizeUrl(baseUrl, clientId, "https://evil.example/steal", method: "S256");
            using var resp = await http.GetAsync(url, ct);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Null(resp.Headers.Location); // a bad redirect must never be redirected to
        }
        finally { await cimd.StopAsync(ct); await plane.StopAsync(ct); }
    }

    [SkippableFact]
    public async Task Authorize_accepts_a_loopback_redirect_on_any_port_per_rfc8252()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var (plane, baseUrl, ct) = await StartPlaneAsync(configureOperator: true);
        await using var host = plane;
        // CIMD registers a loopback callback on one port; the client presents another.
        var (cimd, clientId) = OAuthTestKit.BuildCimdServer("CLI Client", "http://127.0.0.1:5555/callback");
        await cimd.StartAsync(ct);
        try
        {
            using var http = NewClient();
            var url = AuthorizeUrl(baseUrl, clientId, "http://127.0.0.1:61234/callback", method: "S256");
            using var resp = await http.GetAsync(url, ct);
            // Same loopback path, different port → allowed → the consent form renders.
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Contains("Operator passphrase", await resp.Content.ReadAsStringAsync(ct));
        }
        finally { await cimd.StopAsync(ct); await plane.StopAsync(ct); }
    }

    [SkippableFact]
    public async Task Authorize_redirects_a_plain_pkce_as_invalid_request()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var (plane, baseUrl, ct) = await StartPlaneAsync(configureOperator: true);
        await using var host = plane;
        const string redirect = "https://app.example.com/callback";
        var (cimd, clientId) = OAuthTestKit.BuildCimdServer("Client", redirect);
        await cimd.StartAsync(ct);
        try
        {
            using var http = NewClient();
            // redirect_uri is valid, so the PKCE error redirects back with error+state.
            var url = AuthorizeUrl(baseUrl, clientId, redirect, method: "plain");
            using var resp = await http.GetAsync(url, ct);
            Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
            var q = QueryHelpers.ParseQuery(resp.Headers.Location!.Query);
            Assert.Equal("invalid_request", q["error"]);
            Assert.Equal("s", q["state"]); // state echoed even on error
        }
        finally { await cimd.StopAsync(ct); await plane.StopAsync(ct); }
    }

    [SkippableFact]
    public async Task Authorize_redirects_a_resource_mismatch_as_invalid_target()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var (plane, baseUrl, ct) = await StartPlaneAsync(configureOperator: true);
        await using var host = plane;
        const string redirect = "https://app.example.com/callback";
        var (cimd, clientId) = OAuthTestKit.BuildCimdServer("Client", redirect);
        await cimd.StartAsync(ct);
        try
        {
            using var http = NewClient();
            var url = QueryHelpers.AddQueryString($"{baseUrl}/oauth/authorize", new Dictionary<string, string?>
            {
                ["response_type"] = "code",
                ["client_id"] = clientId,
                ["redirect_uri"] = redirect,
                ["code_challenge"] = OAuthTestKit.S256Challenge(OAuthTestKit.NewCodeVerifier()),
                ["code_challenge_method"] = "S256",
                ["state"] = "s",
                ["resource"] = "https://some-other-server.example", // not this instance
            });
            using var resp = await http.GetAsync(url, ct);
            Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
            Assert.Equal("invalid_target", QueryHelpers.ParseQuery(resp.Headers.Location!.Query)["error"]);
        }
        finally { await cimd.StopAsync(ct); await plane.StopAsync(ct); }
    }

    [SkippableFact]
    public async Task Wrong_passphrase_re_renders_the_form_and_mints_no_code()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var (plane, baseUrl, ct) = await StartPlaneAsync(configureOperator: true);
        await using var host = plane;
        const string redirect = "https://app.example.com/callback";
        var (cimd, clientId) = OAuthTestKit.BuildCimdServer("Client", redirect);
        await cimd.StartAsync(ct);
        try
        {
            using var http = NewClient();
            using var resp = await http.PostAsync($"{baseUrl}/oauth/authorize", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["response_type"] = "code",
                    ["client_id"] = clientId,
                    ["redirect_uri"] = redirect,
                    ["code_challenge"] = OAuthTestKit.S256Challenge(OAuthTestKit.NewCodeVerifier()),
                    ["code_challenge_method"] = "S256",
                    ["state"] = "s",
                    ["passphrase"] = "not-the-passphrase",
                }), ct);
            // A wrong passphrase re-renders the form (no redirect, no code granted).
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Null(resp.Headers.Location);
            Assert.Contains("Incorrect operator passphrase", await resp.Content.ReadAsStringAsync(ct));
        }
        finally { await cimd.StopAsync(ct); await plane.StopAsync(ct); }
    }

    // ── Token error paths ───────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Token_rejects_an_unknown_code_as_invalid_grant()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var (plane, baseUrl, ct) = await StartPlaneAsync(configureOperator: true);
        await using var host = plane;
        try
        {
            using var http = NewClient();
            using var resp = await http.PostAsync($"{baseUrl}/oauth/token", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = "dkt_c_nope",
                    ["client_id"] = "https://app.example.com/client.json",
                    ["redirect_uri"] = "https://app.example.com/callback",
                    ["code_verifier"] = "whatever",
                }), ct);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal("invalid_grant", body.GetProperty("error").GetString());
        }
        finally { await plane.StopAsync(ct); }
    }

    [SkippableFact]
    public async Task Token_rejects_a_non_authorization_code_grant()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var (plane, baseUrl, ct) = await StartPlaneAsync(configureOperator: true);
        await using var host = plane;
        try
        {
            using var http = NewClient();
            using var resp = await http.PostAsync($"{baseUrl}/oauth/token", new FormUrlEncodedContent(
                new Dictionary<string, string> { ["grant_type"] = "client_credentials" }), ct);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal("unsupported_grant_type", body.GetProperty("error").GetString());
        }
        finally { await plane.StopAsync(ct); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(WebApplication Plane, string BaseUrl, CancellationToken Ct)> StartPlaneAsync(bool configureOperator)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var port = OAuthTestKit.FreePort();
        var plane = OAuthTestKit.BuildPlane(pg.ConnectionString, port, configureOperator);
        await plane.StartAsync(cts.Token);
        return (plane, OAuthTestKit.BaseUrl(plane), cts.Token);
    }

    private static string AuthorizeUrl(string baseUrl, string clientId, string redirectUri, string method) =>
        QueryHelpers.AddQueryString($"{baseUrl}/oauth/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["code_challenge"] = OAuthTestKit.S256Challenge(OAuthTestKit.NewCodeVerifier()),
            ["code_challenge_method"] = method,
            ["state"] = "s",
        });
}
