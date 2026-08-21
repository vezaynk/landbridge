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
/// MCP Tasks methods project the message envelope. Session id is not task id.
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
    public async Task Create_session_opens_no_task_until_an_envelope_exists()
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

        var missing = await Assert.ThrowsAsync<McpProtocolException>(() => GetTaskAsync(lead, sessionId, ct));
        Assert.Equal(McpErrorCode.InvalidParams, missing.ErrorCode);

        var listed = await ListTasksAsync(lead, ct);
        Assert.Empty(listed["tasks"]!.AsArray());

        var cancel = await Assert.ThrowsAsync<McpProtocolException>(() => CancelTaskAsync(lead, sessionId, ct));
        Assert.Equal(McpErrorCode.InvalidParams, cancel.ErrorCode);
        Assert.Contains("stop_session", cancel.Message, StringComparison.Ordinal);

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task A_question_is_a_task_id_distinct_from_the_session()
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

        Guid messageId;
        await using (var db = pg.NewContext())
        {
            var store = new SessionStore(db, TimeProvider.System);
            var instance = WorkerInstanceId.New();
            Assert.IsType<StoreResult.Applied>(await store.DispatchNextAsync(
                new MachineSnapshot("m1", true, false, new HashSet<string> { "default" }), instance));
            var asked = Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(
                sessionId,
                new RequestInput(new WorkerCaller(team, sessionId, instance),
                    InputRequestKind.Question, "which DB?")));
            messageId = asked.Session.MessageId!.Value;
            Assert.NotEqual(sessionId.Value, messageId);
        }

        await using (var lead = await ConnectAsync(new Uri(baseUrl + "/"), leadToken, ct))
        {
            var got = await GetTaskAsync(lead, messageId.ToString(), ct);
            Assert.Equal(messageId.ToString(), got["taskId"]?.GetValue<string>());
            Assert.Equal("input_required", got["status"]?.GetValue<string>());
            Assert.Contains("send_input_response", got["statusMessage"]?.GetValue<string>(),
                StringComparison.Ordinal);

            var listed = await ListTasksAsync(lead, ct);
            Assert.Equal(messageId.ToString(),
                Assert.Single(listed["tasks"]!.AsArray(), t => t?["status"]?.GetValue<string>() == "input_required")
                    ?["taskId"]?.GetValue<string>());
        }

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Tasks_get_does_not_leak_another_Teams_envelope()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        var baseUrl = app.Urls.First(u => u.StartsWith("http://"));
        var teamA = TeamId.New();
        var a = await ClaimLeadAsync(teamA, ct);
        var b = await ClaimLeadAsync(TeamId.New(), ct);

        SessionId sessionId;
        await using (var leadA = await ConnectAsync(new Uri(baseUrl + "/"), a, ct))
        {
            var created = await leadA.CallToolAsync("create_session", new Dictionary<string, object?>
            {
                ["description"] = "private",
                ["profile"] = "default",
            }, cancellationToken: ct);
            sessionId = new SessionId(Guid.Parse(Assert.Single(created.Content.OfType<TextContentBlock>()).Text));
        }

        Guid messageId;
        await using (var db = pg.NewContext())
        {
            var store = new SessionStore(db, TimeProvider.System);
            var instance = WorkerInstanceId.New();
            Assert.IsType<StoreResult.Applied>(await store.DispatchNextAsync(
                new MachineSnapshot("m1", true, false, new HashSet<string> { "default" }), instance));
            var asked = Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(
                sessionId,
                new RequestInput(new WorkerCaller(teamA, sessionId, instance),
                    InputRequestKind.Question, "secret?")));
            messageId = asked.Session.MessageId!.Value;
        }

        await using (var leadB = await ConnectAsync(new Uri(baseUrl + "/"), b, ct))
        {
            var ex = await Assert.ThrowsAsync<McpProtocolException>(
                () => GetTaskAsync(leadB, messageId.ToString(), ct));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
            var listed = await ListTasksAsync(leadB, ct);
            Assert.Empty(listed["tasks"]!.AsArray());
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
