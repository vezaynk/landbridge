using System.Text.Json;
using Landbridge.Contracts;
using Landbridge.Core;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Tests;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// The <c>open_forward</c> worker tool over a real MCP connection (spec §8.3,
/// §10). The authority gates hold — an unknown service is refused, and a Lead (no
/// worker credential) cannot call it at all — and the happy path returns a
/// <c>{host, port}</c> loopback address, with the plane driving both landbridged ends
/// (here their send delegates are stubbed to report a bound port; the full
/// no-fakes splice loop is <see cref="ForwardDataPlaneEndToEndTests"/>).
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
    public async Task Worker_opens_a_forward_and_receives_a_loopback_host_and_port()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        const int boundPort = 45999; // the port the consumer landbridged would report

        var team = TeamId.New();
        // Producer first (only submitted task → deterministic dispatch), then the
        // consumer worker whose token we drive open_forward with.
        var producerTask = await RelayGrantTestKit.RegisterWorkingServiceAsync(pg, team, "db", ct);
        var consumer = await RelayGrantTestKit.SeedWorkingWorkerAsync(pg, team, ct);

        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, relayValidationBearer: null);
        await plane.StartAsync(ct);
        var baseUri = RelayGrantTestKit.BaseUri(plane);

        // Two "machines" the plane will send the two open-forward commands to. The
        // consumer's stub reports a bound port via the real event sink, exactly as
        // a real consumer landbridged would after binding its loopback listener.
        var registry = plane.Services.GetRequiredService<RunnerConnectionRegistry>();
        var sink = plane.Services.GetRequiredService<RunnerEventSink>();

        registry.Register("mc", new HashSet<string> { "default" }, async (command, token) =>
        {
            if (command is OpenForwardCommand { Role: RelayTunnel.ConsumerRole } c)
                await sink.HandleAsync(new ForwardOpenedEvent(c.Session, c.ForwardId, boundPort), token);
        });
        registry.Register("mp", new HashSet<string> { "default" }, (_, _) => Task.CompletedTask);
        registry.TrackDispatch("mc", consumer.Session);
        registry.TrackDispatch("mp", producerTask);

        await using (var worker = await RelayGrantTestKit.ConnectMcpAsync(baseUri, consumer.Token, ct))
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
            Assert.Equal("127.0.0.1", payload.GetProperty("host").GetString());
            Assert.Equal(boundPort, payload.GetProperty("port").GetInt32());
            Assert.True(Guid.TryParse(payload.GetProperty("forward_id").GetString(), out _));
            Assert.True(payload.TryGetProperty("expires_at", out _));
            // The grant and relay URL are landbridged's business — never handed to the agent.
            Assert.False(payload.TryGetProperty("grant", out _));
            Assert.False(payload.TryGetProperty("relay_url", out _));
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
