using System.Text.Json;
using Docket.Core;
using Docket.ControlPlane.Tests;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Docket.Mcp.Tests;

/// <summary>
/// The <c>open_forward</c> worker tool over a real MCP connection (spec §8.3,
/// §10): a dispatched worker obtains a grant to reach another task's registered
/// service, and the authority gates hold — an unknown service is refused, and a
/// Lead (no worker credential) cannot call it at all. This increment ends here:
/// a worker can obtain a grant over MCP; the docketd data planes are increment 3.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OpenForwardEndToEndTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Worker_opens_a_forward_and_receives_a_grant_forward_id_and_relay_url()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var team = TeamId.New();
        // Producer first (only submitted task → deterministic dispatch), then the
        // consumer worker whose token we drive open_forward with.
        await RelayGrantTestKit.RegisterWorkingServiceAsync(pg, team, "db", ct);
        var workerToken = await RelayGrantTestKit.SeedWorkingWorkerTokenAsync(pg, team, ct);

        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, relayValidationBearer: null);
        await plane.StartAsync(ct);
        var baseUri = RelayGrantTestKit.BaseUri(plane);

        await using (var worker = await RelayGrantTestKit.ConnectMcpAsync(baseUri, workerToken, ct))
        {
            var result = await worker.CallToolAsync("open_forward", new Dictionary<string, object?>
            {
                ["serviceName"] = "db",
            }, cancellationToken: ct);

            Assert.NotEqual(true, result.IsError);
            // A typed tool return surfaces as structured content where the SDK
            // supports it, else as a JSON text block (§7 worker-assignment shape).
            var json = result.StructuredContent is { } structured
                ? structured.GetRawText()
                : string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
            using var doc = JsonDocument.Parse(json);
            var payload = doc.RootElement;
            Assert.StartsWith("dkt_g_", payload.GetProperty("grant").GetString());
            Assert.True(Guid.TryParse(payload.GetProperty("forward_id").GetString(), out _));
            Assert.Equal(Docket.Mcp.Tools.WorkerTools.DefaultRelayUrl, payload.GetProperty("relay_url").GetString());
            Assert.True(payload.TryGetProperty("expires_at", out _));
        }

        await plane.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Unknown_service_is_refused()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var team = TeamId.New();
        var workerToken = await RelayGrantTestKit.SeedWorkingWorkerTokenAsync(pg, team, ct);

        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, relayValidationBearer: null);
        await plane.StartAsync(ct);

        await using var worker = await RelayGrantTestKit.ConnectMcpAsync(RelayGrantTestKit.BaseUri(plane), workerToken, ct);
        var result = await worker.CallToolAsync("open_forward", new Dictionary<string, object?>
        {
            ["serviceName"] = "does-not-exist",
        }, cancellationToken: ct);

        Assert.Equal(true, result.IsError);

        await plane.StopAsync(ct);
    }

    [SkippableFact]
    public async Task A_lead_cannot_open_a_forward()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var team = TeamId.New();
        // Even with a real registered service, a lead credential carries no worker
        // claim, so open_forward is refused at the door (worker-only, §10).
        await RelayGrantTestKit.RegisterWorkingServiceAsync(pg, team, "db", ct);
        var leadToken = await RelayGrantTestKit.LeadTokenAsync(pg, team, ct);

        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, relayValidationBearer: null);
        await plane.StartAsync(ct);

        await using var lead = await RelayGrantTestKit.ConnectMcpAsync(RelayGrantTestKit.BaseUri(plane), leadToken, ct);
        var result = await lead.CallToolAsync("open_forward", new Dictionary<string, object?>
        {
            ["serviceName"] = "db",
        }, cancellationToken: ct);

        Assert.Equal(true, result.IsError);

        await plane.StopAsync(ct);
    }
}
