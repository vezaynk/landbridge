using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Docket.ControlPlane.Auth;
using Docket.Core;
using Docket.ControlPlane.Tests;
using Microsoft.AspNetCore.WebUtilities;
using ModelContextProtocol.Protocol;

namespace Docket.Mcp.Tests;

/// <summary>
/// The crown (spec §5): drive the <b>entire</b> OAuth 2.1 human flow headlessly
/// against the real MCP host with a real Postgres, exactly as an MCP client like
/// Claude Code would — discover the authorization server from the 401 challenge,
/// read both metadata documents, walk authorize (GET form → POST passphrase) with
/// a CIMD-served client, exchange the code at the token endpoint with the PKCE
/// verifier, and then <b>use</b> the returned <c>dkt_h_</c> token to open the real
/// system: claim a Lead with it and call a Lead tool over MCP. That end proves the
/// front door opens onto the actual control plane, not a mock.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OAuthHumanFlowEndToEndTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static HttpClient NewClient() =>
        // Do not follow redirects: we capture the 302 Location to read the code.
        new(new SocketsHttpHandler { AllowAutoRedirect = false });

    [SkippableFact]
    public async Task Full_oauth_flow_mints_a_human_session_that_opens_the_real_system()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var port = OAuthTestKit.FreePort();
        await using var plane = OAuthTestKit.BuildPlane(pg.ConnectionString, port, configureOperator: true);
        await plane.StartAsync(ct);
        var baseUrl = OAuthTestKit.BaseUrl(plane);

        // A CLI client's loopback callback (RFC 8252). It never has to listen — we
        // read the code off the 302 Location ourselves.
        const string redirectUri = "http://127.0.0.1:54321/callback";
        var (cimd, clientId) = OAuthTestKit.BuildCimdServer("Claude Code (test)", redirectUri);
        await cimd.StartAsync(ct);

        try
        {
            using var http = NewClient();

            // ── 1. Discover the AS from the MCP 401 challenge (RFC 9728 §5.1) ──
            // A well-formed but unauthenticated MCP request (a JSON-RPC initialize):
            // the media-type gate passes so authorization runs and challenges.
            using var mcpProbe = new HttpRequestMessage(HttpMethod.Post, baseUrl)
            {
                Content = new StringContent(
                    """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",
                    Encoding.UTF8, "application/json"),
            };
            mcpProbe.Headers.Accept.ParseAdd("application/json");
            mcpProbe.Headers.Accept.ParseAdd("text/event-stream");
            using var unauth = await http.SendAsync(mcpProbe, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);
            var challenge = Assert.Single(unauth.Headers.WwwAuthenticate);
            Assert.Equal("Bearer", challenge.Scheme);
            var resourceMetadataUrl = ExtractParam(challenge.Parameter!, "resource_metadata");
            Assert.Equal($"{baseUrl}/.well-known/oauth-protected-resource", resourceMetadataUrl);

            // ── 2. Protected Resource Metadata (RFC 9728) ──
            var prm = await http.GetFromJsonAsync<JsonElement>(resourceMetadataUrl, ct);
            Assert.Equal(baseUrl, prm.GetProperty("resource").GetString());
            var authServer = prm.GetProperty("authorization_servers")[0].GetString()!;
            Assert.Equal(baseUrl, authServer);

            // ── 3. Authorization Server Metadata (RFC 8414) ──
            var asm = await http.GetFromJsonAsync<JsonElement>(
                $"{authServer}/.well-known/oauth-authorization-server", ct);
            var authorizeEndpoint = asm.GetProperty("authorization_endpoint").GetString()!;
            var tokenEndpoint = asm.GetProperty("token_endpoint").GetString()!;
            Assert.Contains("S256", asm.GetProperty("code_challenge_methods_supported")
                .EnumerateArray().Select(e => e.GetString()));
            Assert.True(asm.GetProperty("client_id_metadata_document_supported").GetBoolean());

            // ── 4. authorize (GET) — the consent form (PKCE S256 + resource) ──
            var verifier = OAuthTestKit.NewCodeVerifier();
            const string state = "opaque-state-value-xyz";
            var authorizeUrl = QueryHelpers.AddQueryString(authorizeEndpoint, new Dictionary<string, string?>
            {
                ["response_type"] = "code",
                ["client_id"] = clientId,
                ["redirect_uri"] = redirectUri,
                ["code_challenge"] = OAuthTestKit.S256Challenge(verifier),
                ["code_challenge_method"] = "S256",
                ["state"] = state,
                ["scope"] = "docket",
                ["resource"] = baseUrl, // RFC 8707: audience-bind to this server
            });

            using var form = await http.GetAsync(authorizeUrl, ct);
            Assert.Equal(HttpStatusCode.OK, form.StatusCode);
            var formHtml = await form.Content.ReadAsStringAsync(ct);
            Assert.Contains("Operator passphrase", formHtml);
            Assert.Contains("Claude Code (test)", formHtml); // client name from CIMD

            // ── 5. authorize (POST) — submit the passphrase; expect a 302 back ──
            using var authResp = await http.PostAsync(authorizeEndpoint, new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["response_type"] = "code",
                    ["client_id"] = clientId,
                    ["redirect_uri"] = redirectUri,
                    ["code_challenge"] = OAuthTestKit.S256Challenge(verifier),
                    ["code_challenge_method"] = "S256",
                    ["state"] = state,
                    ["scope"] = "docket",
                    ["resource"] = baseUrl,
                    ["passphrase"] = OAuthTestKit.Passphrase,
                }), ct);

            Assert.Equal(HttpStatusCode.Redirect, authResp.StatusCode);
            var location = authResp.Headers.Location!;
            Assert.StartsWith(redirectUri, location.ToString());
            var redirectQuery = QueryHelpers.ParseQuery(location.Query);
            Assert.Equal(state, redirectQuery["state"]); // state echoed verbatim
            var code = redirectQuery["code"].ToString();
            Assert.StartsWith("dkt_c_", code);

            // ── 6. token — exchange the code with the PKCE verifier ──
            using var tokenResp = await http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["client_id"] = clientId,
                    ["redirect_uri"] = redirectUri,
                    ["code_verifier"] = verifier,
                    ["resource"] = baseUrl,
                }), ct);

            Assert.Equal(HttpStatusCode.OK, tokenResp.StatusCode);
            Assert.Equal("no-store", tokenResp.Headers.CacheControl!.ToString());
            var token = await tokenResp.Content.ReadFromJsonAsync<JsonElement>(ct);
            var accessToken = token.GetProperty("access_token").GetString()!;
            Assert.StartsWith("dkt_h_", accessToken); // the EXISTING opaque human session
            Assert.Equal("Bearer", token.GetProperty("token_type").GetString());
            Assert.True(token.GetProperty("expires_in").GetInt32() > 0);

            // ── 7. USE the token: it is a real §5 human session — claim a Lead
            //       with it and drive a Lead tool over MCP, onto the real store. ──
            var team = TeamId.New();
            string leadToken;
            await using (var db = pg.NewContext())
            {
                var tokens = new TokenService(db, TimeProvider.System);
                // ClaimLeadAsync validates the presented token as a live human
                // session (§5) before minting the lead claim — so this succeeding
                // is the proof the OAuth-minted token is the genuine article.
                var claim = Assert.IsType<LeadClaimResult.Claimed>(
                    await tokens.ClaimLeadAsync(accessToken, team, ct: ct));
                leadToken = claim.Token.Token;
            }

            await using var lead = await OAuthTestKit.ConnectMcpAsync(new Uri(baseUrl + "/"), leadToken, ct);
            var created = await lead.CallToolAsync("create_task", new Dictionary<string, object?>
            {
                ["description"] = "prove the front door opens onto the real system",
                ["completionCriteria"] = "a task exists",
                ["mode"] = "lead",
                ["profile"] = null,
                ["workspace"] = null,
            }, cancellationToken: ct);

            Assert.NotEqual(true, created.IsError);
            var taskId = Assert.Single(created.Content.OfType<TextContentBlock>()).Text;
            Assert.True(Guid.TryParse(taskId, out _));
        }
        finally
        {
            await cimd.StopAsync(ct);
            await plane.StopAsync(ct);
        }
    }

    /// <summary>Pulls a quoted parameter (e.g. <c>resource_metadata="..."</c>) out of a WWW-Authenticate value.</summary>
    private static string ExtractParam(string parameter, string name)
    {
        foreach (var part in parameter.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith(name + "=", StringComparison.Ordinal))
                return trimmed[(name.Length + 1)..].Trim('"');
        }

        throw new InvalidOperationException($"'{name}' not found in WWW-Authenticate: {parameter}");
    }
}
