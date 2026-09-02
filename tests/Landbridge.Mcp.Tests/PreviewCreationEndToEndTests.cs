using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Landbridge.Mcp.Dashboard;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// The two §8.4 mint surfaces end to end: worker <c>open_preview</c> and the §12
/// dashboard Create-preview POST. Each mints a public (capability URL) and a gated
/// (private, operator session) mapping, then a browser hits the real preview
/// frontend through the real relay — the crown e2e in
/// <see cref="PreviewEndToEndTests"/> mints via the store directly, so it never
/// proves a URL a worker or the dashboard actually handed out.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PreviewCreationEndToEndTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string RelayBearer = "relay-shared-secret-under-test";
    private const string PreviewBearer = "preview-shared-secret-under-test";
    private const string Domain = "preview.localhost";

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Worker_open_preview_public_url_serves_through_the_real_relay()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var stack = await Stack.StartAsync(pg, ct);
        var minted = await WorkerMintAsync(stack, isPublic: true, ttlMinutes: 10, ct);

        Assert.Equal("public", minted.Auth);
        AssertExpiresIn(minted.ExpiresAt, TimeSpan.FromMinutes(10));
        await AssertPublicServesAsync(stack, minted.Label, ct);
    }

    [SkippableFact]
    public async Task Worker_open_preview_gated_url_completes_the_browser_auth_flow()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var stack = await Stack.StartAsync(pg, ct);
        var minted = await WorkerMintAsync(stack, isPublic: false, ttlMinutes: null, ct);

        Assert.Equal("gated", minted.Auth);
        AssertExpiresIn(minted.ExpiresAt, PreviewMint.GatedDefaultTtl);
        await AssertGatedRedirectsThenServesAsync(stack, minted.Label, ct);
    }

    [SkippableFact]
    public async Task Dashboard_create_public_preview_url_serves_through_the_real_relay()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var stack = await Stack.StartAsync(pg, ct);
        var minted = await DashboardMintAsync(stack, auth: "public", ttlMinutes: 10, ct);

        Assert.Equal("public", minted.Auth);
        AssertExpiresIn(minted.ExpiresAt, TimeSpan.FromMinutes(10));
        await AssertPublicServesAsync(stack, minted.Label, ct);
    }

    [SkippableFact]
    public async Task Dashboard_create_gated_preview_url_completes_the_browser_auth_flow()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var stack = await Stack.StartAsync(pg, ct);
        var minted = await DashboardMintAsync(stack, auth: "gated", ttlMinutes: null, ct);

        Assert.Equal("gated", minted.Auth);
        AssertExpiresIn(minted.ExpiresAt, PreviewMint.GatedDefaultTtl);
        await AssertGatedRedirectsThenServesAsync(stack, minted.Label, ct);
    }

    [SkippableFact]
    public async Task Dashboard_create_preview_without_a_session_is_unauthorized()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var stack = await Stack.StartAsync(pg, ct);
        using var http = new HttpClient { BaseAddress = stack.PlaneBase };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/dashboard/preview?format=json")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["teamId"] = stack.Team.Value.ToString(),
                ["service"] = $"{stack.Session}:web",
                ["auth"] = "public",
            }),
        };
        using var response = await http.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Mint surfaces ──────────────────────────────────────────────────────────

    private static async Task<Minted> WorkerMintAsync(
        Stack stack, bool isPublic, int? ttlMinutes, CancellationToken ct)
    {
        await using var mcp = await RelayGrantTestKit.ConnectMcpAsync(stack.PlaneBase, stack.WorkerToken, ct);
        var args = new Dictionary<string, object?> { ["serviceName"] = "web", ["isPublic"] = isPublic };
        if (ttlMinutes is { } minutes)
            args["ttlMinutes"] = minutes;

        var json = ParseTool(await mcp.CallToolAsync("open_preview", args, cancellationToken: ct));
        return ReadMint(json, url: "url", auth: "auth", expires: "expires_at");
    }

    private static async Task<Minted> DashboardMintAsync(
        Stack stack, string auth, int? ttlMinutes, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = stack.PlaneBase };
        var form = new Dictionary<string, string>
        {
            ["teamId"] = stack.Team.Value.ToString(),
            ["service"] = $"{stack.Session}:web",
            ["auth"] = auth,
        };
        if (ttlMinutes is { } minutes)
            form["ttl"] = minutes.ToString();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/dashboard/preview?format=json")
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Add("Cookie", $"{DashboardAuth.CookieName}={stack.OperatorToken}");
        using var response = await http.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return ReadMint(body, url: "url", auth: "auth", expires: "expiresAt");
    }

    // ── Browser against the minted URL ─────────────────────────────────────────

    private static async Task AssertPublicServesAsync(Stack stack, string label, CancellationToken ct)
    {
        using var browser = stack.Frontend.Browser();
        using var response = await browser.GetAsync($"http://{label}.{Domain}/deep/path?x=1", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello from upstream", await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("/deep/path?x=1", response.Headers.GetValues("X-Echo-Path").First());
    }

    private static async Task AssertGatedRedirectsThenServesAsync(Stack stack, string label, CancellationToken ct)
    {
        var planeBase = stack.PlaneBase.ToString().TrimEnd('/');

        using var browser = stack.Frontend.Browser(followRedirects: false);
        var authRedirect = (await browser.GetAsync($"http://{label}.{Domain}/app", ct)).Headers.Location!.ToString();
        Assert.StartsWith($"{planeBase}/dashboard/preview-auth", authRedirect);

        using var dashboard = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var authReq = new HttpRequestMessage(HttpMethod.Get, authRedirect);
        authReq.Headers.Add("Cookie", $"{DashboardAuth.CookieName}={stack.OperatorToken}");
        var back = (await dashboard.SendAsync(authReq, ct)).Headers.Location!.ToString();
        Assert.Contains("landbridge_preview_code=", back);

        using var exchange = await browser.GetAsync(back, ct);
        Assert.Equal(HttpStatusCode.Found, exchange.StatusCode);
        var previewCookie = Assert.Single(exchange.Headers.GetValues("Set-Cookie")).Split(';')[0];
        Assert.StartsWith("landbridge_preview=", previewCookie);

        using var content = new HttpRequestMessage(HttpMethod.Get, $"http://{label}.{Domain}/app");
        content.Headers.Add("Cookie", previewCookie);
        using var final = await browser.SendAsync(content, ct);
        Assert.Equal(HttpStatusCode.OK, final.StatusCode);
        Assert.Equal("hello from upstream", await final.Content.ReadAsStringAsync(ct));
    }

    // ── Stack: plane + relay + producer landbridged + frontend ─────────────────

    private sealed class Stack : IAsyncDisposable
    {
        public required TeamId Team { get; init; }
        public required SessionId Session { get; init; }
        public required string WorkerToken { get; init; }
        public required string OperatorToken { get; init; }
        public required PreviewUpstream Upstream { get; init; }
        public required WebApplication Plane { get; init; }
        public required WebApplication Relay { get; init; }
        public required DaemonHarness Producer { get; init; }
        public required PreviewFrontendHarness Frontend { get; init; }
        public required Uri PlaneBase { get; init; }

        public static async Task<Stack> StartAsync(PostgresFixture pg, CancellationToken ct)
        {
            var upstream = await PreviewUpstream.StartAsync();
            var team = TeamId.New();
            var worker = await RelayGrantTestKit.SeedWorkingWorkerAsync(pg, team, ct);
            string operatorToken;
            await using (var db = pg.NewContext())
                operatorToken = (await new TokenService(db, TimeProvider.System).IssueHumanSessionAsync(ct)).Token;

            var relayUrl = RelayGrantTestKit.ReserveLoopbackUrl();
            var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, RelayBearer, relayUrl, PreviewBearer);
            await plane.StartAsync(ct);
            var relay = RelayGrantTestKit.BuildRelay(
                RelayGrantTestKit.BaseUri(plane).ToString(), RelayBearer, listenUrl: relayUrl);
            await relay.StartAsync(ct);

            var registry = plane.Services.GetRequiredService<RunnerConnectionRegistry>();
            var producer = new DaemonHarness(
                "mp", new SinkForwardingChannel(plane.Services.GetRequiredService<RunnerEventSink>()));
            await producer.StartAsync();
            registry.Register("mp", new HashSet<string> { "default" }, producer.Send);
            registry.TrackDispatch("mp", worker.Session);

            var planeBase = RelayGrantTestKit.BaseUri(plane);
            var frontend = PreviewFrontendHarness.Start(planeBase.ToString(), dashboardUrl: planeBase.ToString());

            await using (var mcp = await RelayGrantTestKit.ConnectMcpAsync(planeBase, worker.Token, ct))
            {
                await mcp.CallToolAsync("register_service",
                    new Dictionary<string, object?> { ["name"] = "web", ["port"] = upstream.Port },
                    cancellationToken: ct);
            }

            return new Stack
            {
                Team = team,
                Session = worker.Session,
                WorkerToken = worker.Token,
                OperatorToken = operatorToken,
                Upstream = upstream,
                Plane = plane,
                Relay = relay,
                Producer = producer,
                Frontend = frontend,
                PlaneBase = planeBase,
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Producer.DisposeAsync();
            await Frontend.DisposeAsync();
            await Relay.StopAsync();
            await Relay.DisposeAsync();
            await Plane.StopAsync();
            await Plane.DisposeAsync();
            await Upstream.DisposeAsync();
        }
    }

    private sealed record Minted(string Label, string Auth, DateTimeOffset ExpiresAt);

    private static Minted ReadMint(JsonElement json, string url, string auth, string expires)
    {
        var href = json.GetProperty(url).GetString();
        Assert.False(string.IsNullOrEmpty(href));
        Assert.Contains($".{Domain}", href);
        var host = new Uri(href).Host;
        var label = host[..host.IndexOf('.')];
        return new Minted(label, json.GetProperty(auth).GetString()!, json.GetProperty(expires).GetDateTimeOffset());
    }

    private static JsonElement ParseTool(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        var text = result.StructuredContent is { } structured
            ? structured.GetRawText()
            : string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static void AssertExpiresIn(DateTimeOffset expiresAt, TimeSpan expected)
    {
        var remaining = expiresAt - DateTimeOffset.UtcNow;
        Assert.InRange(remaining, expected - TimeSpan.FromMinutes(1), expected + TimeSpan.FromMinutes(1));
    }
}
