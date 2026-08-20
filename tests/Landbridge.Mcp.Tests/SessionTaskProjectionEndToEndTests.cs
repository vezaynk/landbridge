using System.Text.Json;
using System.Text.Json.Nodes;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Landbridge.Mcp;
using Landbridge.Mcp.Auth;
using Landbridge.Mcp.Tools;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// MCP Tasks methods project occupancy + the message machine. Session id is task id.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SessionTaskProjectionEndToEndTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Create_session_is_a_working_task_the_Lead_can_get_list_and_cancel()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        var baseUrl = app.Urls.First(u => u.StartsWith("http://"));
        var team = TeamId.New();
        var leadToken = await ClaimLeadAsync(team, ct);

        await using var lead = await ConnectAsync(new Uri(baseUrl + "/"), leadToken, ct);

        Assert.True(lead.ServerCapabilities.Extensions?.ContainsKey(SessionTaskProjection.ExtensionId) == true
                    || lead.ServerCapabilities.Experimental?.ContainsKey(SessionTaskProjection.ExtensionId) == true);

        var created = await lead.CallToolAsync("create_session", new Dictionary<string, object?>
        {
            ["description"] = "project me",
            ["profile"] = "default",
        }, cancellationToken: ct);
        var sessionId = Assert.Single(created.Content.OfType<TextContentBlock>()).Text;

        var got = await GetTaskAsync(lead, sessionId, ct);
        Assert.Equal(sessionId, got["taskId"]?.GetValue<string>());
        Assert.Equal("working", got["status"]?.GetValue<string>());
        Assert.Equal(5000, got["pollInterval"]?.GetValue<int>());

        var listed = await ListTasksAsync(lead, ct);
        var listedId = listed["tasks"]!.AsArray()
            .Select(t => t?["taskId"]?.GetValue<string>())
            .Single();
        Assert.Equal(sessionId, listedId);

        var cancelled = await CancelTaskAsync(lead, sessionId, ct);
        Assert.Equal("cancelled", cancelled["status"]?.GetValue<string>());

        var after = await GetTaskAsync(lead, sessionId, ct);
        Assert.Equal("cancelled", after["status"]?.GetValue<string>());

        var ex = await Assert.ThrowsAsync<McpProtocolException>(() => CancelTaskAsync(lead, sessionId, ct));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Tasks_get_does_not_leak_another_Teams_session()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        var baseUrl = app.Urls.First(u => u.StartsWith("http://"));
        var a = await ClaimLeadAsync(TeamId.New(), ct);
        var b = await ClaimLeadAsync(TeamId.New(), ct);

        string sessionId;
        await using (var leadA = await ConnectAsync(new Uri(baseUrl + "/"), a, ct))
        {
            var created = await leadA.CallToolAsync("create_session", new Dictionary<string, object?>
            {
                ["description"] = "private",
                ["profile"] = "default",
            }, cancellationToken: ct);
            sessionId = Assert.Single(created.Content.OfType<TextContentBlock>()).Text;
        }

        await using (var leadB = await ConnectAsync(new Uri(baseUrl + "/"), b, ct))
        {
            var ex = await Assert.ThrowsAsync<McpProtocolException>(() => GetTaskAsync(leadB, sessionId, ct));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
            var listed = await ListTasksAsync(leadB, ct);
            Assert.Empty(listed["tasks"]!.AsArray());
        }

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task A_question_projects_as_input_required()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        var baseUrl = app.Urls.First(u => u.StartsWith("http://"));
        var team = TeamId.New();
        var leadToken = await ClaimLeadAsync(team, ct);

        SessionId sessionId;
        await using (var lead = await ConnectAsync(new Uri(baseUrl + "/"), leadToken, ct))
        {
            var created = await lead.CallToolAsync("create_session", new Dictionary<string, object?>
            {
                ["description"] = "ask then wait",
                ["profile"] = "default",
            }, cancellationToken: ct);
            sessionId = new SessionId(Guid.Parse(Assert.Single(created.Content.OfType<TextContentBlock>()).Text));
        }

        await using (var db = pg.NewContext())
        {
            var store = new SessionStore(db, TimeProvider.System);
            var instance = WorkerInstanceId.New();
            Assert.IsType<StoreResult.Applied>(await store.DispatchNextAsync(
                new MachineSnapshot("m1", true, false, new HashSet<string> { "default" }), instance));
            Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(
                sessionId,
                new RequestInput(new WorkerCaller(team, sessionId, instance),
                    InputRequestKind.Question, "which DB?")));
        }

        await using (var lead = await ConnectAsync(new Uri(baseUrl + "/"), leadToken, ct))
        {
            var got = await GetTaskAsync(lead, sessionId.ToString(), ct);
            Assert.Equal("input_required", got["status"]?.GetValue<string>());
            Assert.Contains("answer_input_request", got["statusMessage"]?.GetValue<string>(),
                StringComparison.Ordinal);
        }

        await app.StopAsync(ct);
    }

    private async Task<string> ClaimLeadAsync(TeamId team, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        var human = await tokens.IssueHumanSessionAsync(ct);
        var claim = Assert.IsType<LeadClaimResult.Claimed>(await tokens.ClaimLeadAsync(human.Token, team, ct: ct));
        return claim.Token.Token;
    }

    private WebApplication BuildServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddDbContext<LandbridgeDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddLandbridgeStore();
        builder.Services.AddScoped<RelayGrantService>();
        builder.Services.AddScoped<PreviewMappingService>();
        builder.Services.AddScoped<TokenService>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<RunnerConnectionRegistry>();
        builder.Services.AddLandbridgeForwarding();
        builder.Services.AddHttpContextAccessor();

        builder.Services.AddAuthentication(LandbridgeAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, LandbridgeAuthenticationHandler>(
                LandbridgeAuthenticationHandler.SchemeName, configureOptions: null);
        builder.Services.AddAuthorization();

        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<WorkerTools>()
            .WithTools<LeadTools>()
            .WithSessionTaskProjection();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMcp().RequireAuthorization();
        return app;
    }

    private static async Task<McpClient> ConnectAsync(Uri endpoint, string bearer, CancellationToken ct)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearer}" },
        });
        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    private static async Task<JsonObject> GetTaskAsync(McpClient client, string taskId, CancellationToken ct) =>
        await SendAsync(client, "tasks/get", new { taskId }, ct);

    private static async Task<JsonObject> ListTasksAsync(McpClient client, CancellationToken ct) =>
        await SendAsync(client, "tasks/list", new { }, ct);

    private static async Task<JsonObject> CancelTaskAsync(McpClient client, string taskId, CancellationToken ct) =>
        await SendAsync(client, "tasks/cancel", new { taskId }, ct);

    private static async Task<JsonObject> SendAsync<T>(McpClient client, string method, T parameters, CancellationToken ct)
    {
        var response = await client.SendRequestAsync(new JsonRpcRequest
        {
            Method = method,
            Params = JsonSerializer.SerializeToNode(parameters),
        }, ct);
        return Assert.IsType<JsonObject>(response.Result);
    }
}
