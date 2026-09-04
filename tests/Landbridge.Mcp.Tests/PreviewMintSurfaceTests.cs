using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// The two §8.4 mint surfaces (#62): the worker <c>open_preview</c> MCP tool and the
/// §12 dashboard 'Create preview' POST. Both mint the same mapping and return a
/// shareable URL; the worker path is task-scoped (only its own registered service),
/// the dashboard path is operator-gated.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PreviewMintSurfaceTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Open_preview_tool_mints_a_url_for_an_owned_service_only()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var ct = cts.Token;
        var team = TeamId.New();

        var worker = await RelayGrantTestKit.SeedWorkingWorkerAsync(pg, team, ct);
        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, relayValidationBearer: null);
        await plane.StartAsync(ct);
        await using var mcp = await RelayGrantTestKit.ConnectMcpAsync(
            RelayGrantTestKit.BaseUri(plane), worker.Token, ct);

        await mcp.CallToolAsync("register_service",
            new Dictionary<string, object?> { ["name"] = "web", ["port"] = 3000 }, cancellationToken: ct);

        // Gated preview for the owned service → an https-shaped preview URL.
        var gated = ParseTool(await mcp.CallToolAsync("open_preview",
            new Dictionary<string, object?> { ["serviceName"] = "web" }, cancellationToken: ct));
        Assert.Equal("gated", gated.GetProperty("auth").GetString());
        Assert.Contains(".preview.localhost", gated.GetProperty("url").GetString());

        // Public preview with a TTL.
        var pub = ParseTool(await mcp.CallToolAsync("open_preview",
            new Dictionary<string, object?> { ["serviceName"] = "web", ["isPublic"] = true, ["ttlMinutes"] = 10 },
            cancellationToken: ct));
        Assert.Equal("public", pub.GetProperty("auth").GetString());

        // A service this task did not register is not previewable (task-scoped, §8.4).
        var bad = await mcp.CallToolAsync("open_preview",
            new Dictionary<string, object?> { ["serviceName"] = "ghost" }, cancellationToken: ct);
        Assert.Equal(true, bad.IsError);

        await plane.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Dashboard_mint_creates_a_preview_for_a_registered_service()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var ct = cts.Token;
        var team = TeamId.New();

        var producerTask = await RelayGrantTestKit.RegisterWorkingServiceAsync(pg, team, "web", ct, port: 3000);
        var operatorSession = await RelayGrantTestKit.LeadTokenAsync(pg, team, ct);

        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, relayValidationBearer: null);
        await plane.StartAsync(ct);

        using var http = new HttpClient { BaseAddress = RelayGrantTestKit.BaseUri(plane) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/dashboard/preview?format=json")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["teamId"] = team.Value.ToString(),
                ["service"] = $"{producerTask}:web",
                ["auth"] = "public",
                ["ttl"] = "10",
            }),
        };
        request.Headers.Add("Cookie", $"landbridge_session={operatorSession}");

        using var response = await http.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("public", body.GetProperty("auth").GetString());
        Assert.Contains(".preview.localhost", body.GetProperty("url").GetString());

        await plane.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Dashboard_auth_post_flips_a_preview_to_public()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var ct = cts.Token;
        var team = TeamId.New();

        var producerTask = await RelayGrantTestKit.RegisterWorkingServiceAsync(pg, team, "web", ct, port: 3000);
        var lead = await RelayGrantTestKit.LeadSessionAsync(pg, team, ct);
        Guid previewId;
        await using (var db = pg.NewContext())
        {
            var mint = await new PreviewMappingService(db, TimeProvider.System).CreateAsync(
                team, producerTask, "web", PreviewAuthPolicy.Gated, TimeSpan.FromHours(2), ct);
            previewId = mint.Mapping.Id;
        }

        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, relayValidationBearer: null);
        await plane.StartAsync(ct);

        using var http = new HttpClient { BaseAddress = RelayGrantTestKit.BaseUri(plane) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/dashboard/preview/auth?format=json")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["previewId"] = previewId.ToString(),
                ["public"] = "true",
            }),
        };
        request.Headers.Add("Cookie", $"landbridge_session={lead.HumanToken}");
        request.Headers.Add("Origin", RelayGrantTestKit.BaseUri(plane).GetLeftPart(UriPartial.Authority));

        using var response = await http.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("public", body.GetProperty("auth").GetString());
        Assert.Equal(previewId, body.GetProperty("previewId").GetGuid());

        await using (var db = pg.NewContext())
        {
            var row = await db.PreviewMappings.SingleAsync(m => m.Id == previewId, ct);
            Assert.Equal(PreviewAuthPolicy.Public, row.AuthPolicy);
        }

        await plane.StopAsync(ct);
    }

    private static JsonElement ParseTool(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        var json = result.StructuredContent is { } structured
            ? structured.GetRawText()
            : string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
