using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Docket.Contracts;
using Docket.ControlPlane;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Mcp.Dashboard;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Docket.Mcp.Tests;

/// <summary>
/// The §8.3 <b>human path</b> over real MCP: a Lead binds its human's enrolled
/// machine, then <c>open_lead_forward</c> hands back a loopback address on that
/// machine — how a person reaches a raw-TCP service they could not otherwise touch.
///
/// <para>The first test is the crown, with <b>no fakes</b>: a real relay, a real TCP
/// echo service registered by a working producer task, and two real docketd data
/// planes (the same <see cref="DaemonHarness"/>/<see cref="EchoService"/> rig as
/// <see cref="ForwardDataPlaneEndToEndTests"/>), where the consumer end is the
/// machine the Lead bound rather than a machine a task was dispatched to. Bytes
/// written to the returned address round-trip through the real splice. The rest are
/// the authority gates and the binding's visibility.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LeadForwardEndToEndTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string Bearer = "relay-shared-secret-under-test";

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Lead_forward_from_its_bound_machine_round_trips_through_the_real_relay()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        // ── The producer's registered service: a real loopback TCP echo ─────────
        await using var echo = await EchoService.StartAsync();

        var team = TeamId.New();
        var producerTask = await RelayGrantTestKit.RegisterWorkingServiceAsync(pg, team, "db", ct, port: echo.Port);
        var lead = await RelayGrantTestKit.LeadSessionAsync(pg, team, ct);
        // The human's own box, enrolled exactly like any other machine (§11) — what
        // makes it *theirs* is the binding, not the enrollment.
        var leadMachine = await RelayGrantTestKit.EnrollMachineAsync(pg, "leads-laptop", ct);

        // ── Plane + relay on a pre-reserved relay URL (no build-order race) ─────
        var relayUrl = RelayGrantTestKit.ReserveLoopbackUrl();
        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, Bearer, relayUrl);
        await plane.StartAsync(ct);
        await using var relay = RelayGrantTestKit.BuildRelay(
            RelayGrantTestKit.BaseUri(plane).ToString(), Bearer, listenUrl: relayUrl);
        await relay.StartAsync(ct);

        var registry = plane.Services.GetRequiredService<RunnerConnectionRegistry>();
        var sink = plane.Services.GetRequiredService<RunnerEventSink>();

        // ── Two real docketd data planes: the human's machine and the producer's ─
        var consumerChannel = new SinkForwardingChannel(sink);
        var producerChannel = new SinkForwardingChannel(sink);
        await using var consumerDaemon = new DaemonHarness(leadMachine.ToString(), consumerChannel);
        await using var producerDaemon = new DaemonHarness("mp", producerChannel);
        await consumerDaemon.StartAsync();
        await producerDaemon.StartAsync();

        // The human's machine is registered under its enrolled id — the key the
        // binding resolves to — and holds no dispatched task at all.
        registry.Register(leadMachine.ToString(), new HashSet<string> { "default" }, consumerDaemon.Send);
        registry.Register("mp", new HashSet<string> { "default" }, producerDaemon.Send);
        registry.TrackDispatch("mp", producerTask);

        string forwardId;
        int port;
        await using (var leadClient = await RelayGrantTestKit.ConnectMcpAsync(
                         RelayGrantTestKit.BaseUri(plane), lead.Token, ct))
        {
            // Without a binding there is nowhere to bind a port, and the refusal says
            // exactly what to do about it.
            var unbound = await leadClient.CallToolAsync("open_lead_forward", new Dictionary<string, object?>
            {
                ["serviceName"] = "db",
            }, cancellationToken: ct);
            Assert.Equal(true, unbound.IsError);
            var unboundText = ErrorText(unbound);
            Assert.Contains("no machine bound", unboundText, StringComparison.Ordinal);
            Assert.Contains("/docket-enroll", unboundText, StringComparison.Ordinal);
            Assert.Contains("bind_machine", unboundText, StringComparison.Ordinal);

            var bind = await leadClient.CallToolAsync("bind_machine", new Dictionary<string, object?>
            {
                ["machineId"] = leadMachine.ToString(),
            }, cancellationToken: ct);
            Assert.NotEqual(true, bind.IsError);
            Assert.Contains("leads-laptop", Text(bind), StringComparison.Ordinal);

            // The binding shows up on the reattachment surface (§4, §10).
            var state = await leadClient.CallToolAsync(
                "get_team_state", new Dictionary<string, object?>(), cancellationToken: ct);
            using (var stateDoc = JsonDocument.Parse(Payload(state)))
            {
                var boundMachine = stateDoc.RootElement.GetProperty("boundMachine");
                Assert.Equal(leadMachine, boundMachine.GetProperty("machineId").GetGuid());
                Assert.Equal("leads-laptop", boundMachine.GetProperty("machineName").GetString());
            }

            var result = await leadClient.CallToolAsync("open_lead_forward", new Dictionary<string, object?>
            {
                ["serviceName"] = "db",
            }, cancellationToken: ct);

            Assert.NotEqual(true, result.IsError);
            using var doc = JsonDocument.Parse(Payload(result));
            var payload = doc.RootElement;
            Assert.Equal("127.0.0.1", payload.GetProperty("host").GetString());
            port = payload.GetProperty("port").GetInt32();
            forwardId = payload.GetProperty("forward_id").GetString()!;
            Assert.True(port > 0);
            // The grant and relay URL are docketd's business — never handed to the agent.
            Assert.False(payload.TryGetProperty("grant", out _));
            Assert.False(payload.TryGetProperty("relay_url", out _));
        }

        // ── The human's local client connects and the byte path is real ─────────
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port, ct);
            await using var stream = client.GetStream();

            var payload = new byte[64 * 1024];
            Random.Shared.NextBytes(payload);
            await stream.WriteAsync(payload, ct);
            var echoed = await ReadExactlyAsync(stream, payload.Length, ct);
            Assert.True(payload.AsSpan().SequenceEqual(echoed),
                "bytes did not round-trip lead's docketd → relay → producer-docketd → echo");

            client.Client.Shutdown(SocketShutdown.Both);
        }

        Assert.True(
            await WaitForAsync(() => consumerChannel.HasForwardClosed(forwardId), TimeSpan.FromSeconds(20)),
            "the lead machine's docketd never reported forward-closed");
        Assert.True(
            await WaitForAsync(() => producerChannel.HasForwardClosed(forwardId), TimeSpan.FromSeconds(20)),
            "producer docketd never reported forward-closed");

        // ── The binding is visible to an operator too (§12 JSON twin) ───────────
        var machinesJson = await GetDashboardJsonAsync(plane, "/dashboard/machines?format=json", lead.HumanToken, ct);
        using (var doc = JsonDocument.Parse(machinesJson))
        {
            var bound = Assert.Single(doc.RootElement.EnumerateArray()
                .Where(m => m.GetProperty("machineId").GetString() == leadMachine.ToString())
                .ToList());
            Assert.Equal(lead.HumanId, bound.GetProperty("boundToHuman").GetGuid());
            Assert.True(bound.TryGetProperty("boundAt", out var boundAt) && boundAt.ValueKind != JsonValueKind.Null);
            // The producer's machine belongs to nobody.
            var producerMachine = Assert.Single(doc.RootElement.EnumerateArray()
                .Where(m => m.GetProperty("machineId").GetString() == "mp")
                .ToList());
            Assert.Equal(JsonValueKind.Null, producerMachine.GetProperty("boundToHuman").ValueKind);
        }

        await consumerDaemon.StopAsync();
        await producerDaemon.StopAsync();
        await relay.StopAsync(ct);
        await plane.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Unbind_releases_the_binding_and_open_lead_forward_refuses_again()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var team = TeamId.New();
        await RelayGrantTestKit.RegisterWorkingServiceAsync(pg, team, "db", ct);
        var lead = await RelayGrantTestKit.LeadSessionAsync(pg, team, ct);
        var machine = await RelayGrantTestKit.EnrollMachineAsync(pg, "leads-laptop", ct);

        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, relayValidationBearer: null);
        await plane.StartAsync(ct);

        await using var leadClient = await RelayGrantTestKit.ConnectMcpAsync(
            RelayGrantTestKit.BaseUri(plane), lead.Token, ct);

        Assert.NotEqual(true, (await leadClient.CallToolAsync("bind_machine", new Dictionary<string, object?>
        {
            ["machineId"] = machine.ToString(),
        }, cancellationToken: ct)).IsError);

        var released = await leadClient.CallToolAsync(
            "unbind_machine", new Dictionary<string, object?>(), cancellationToken: ct);
        Assert.NotEqual(true, released.IsError);
        Assert.Contains("leads-laptop", Text(released), StringComparison.Ordinal);

        // Gone from team state, and the forward refuses with the same actionable text.
        var state = await leadClient.CallToolAsync(
            "get_team_state", new Dictionary<string, object?>(), cancellationToken: ct);
        using (var doc = JsonDocument.Parse(Payload(state)))
            AssertNoBoundMachine(doc.RootElement);

        var refused = await leadClient.CallToolAsync("open_lead_forward", new Dictionary<string, object?>
        {
            ["serviceName"] = "db",
        }, cancellationToken: ct);
        Assert.Equal(true, refused.IsError);
        Assert.Contains("no machine bound", ErrorText(refused), StringComparison.Ordinal);

        // A second unbind is a no-op, not a failure.
        var again = await leadClient.CallToolAsync(
            "unbind_machine", new Dictionary<string, object?>(), cancellationToken: ct);
        Assert.NotEqual(true, again.IsError);
        Assert.Contains("no machine bound", Text(again), StringComparison.Ordinal);

        await plane.StopAsync(ct);
    }

    [SkippableFact]
    public async Task A_lead_of_another_team_cannot_forward_the_service()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var owningTeam = TeamId.New();
        var producerTask = await RelayGrantTestKit.RegisterWorkingServiceAsync(pg, owningTeam, "secret-db", ct);
        // A Lead of a *different* Team, with a bound and connected machine — the only
        // thing missing is Team membership (§8.2, §9 check 11).
        var outsider = await RelayGrantTestKit.LeadSessionAsync(pg, TeamId.New(), ct);
        var machine = await RelayGrantTestKit.EnrollMachineAsync(pg, "outsiders-laptop", ct);

        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, relayValidationBearer: null);
        await plane.StartAsync(ct);
        var registry = plane.Services.GetRequiredService<RunnerConnectionRegistry>();
        registry.Register(machine.ToString(), new HashSet<string> { "default" }, (_, _) => Task.CompletedTask);
        registry.Register("mp", new HashSet<string> { "default" }, (_, _) => Task.CompletedTask);
        registry.TrackDispatch("mp", producerTask);

        await using var leadClient = await RelayGrantTestKit.ConnectMcpAsync(
            RelayGrantTestKit.BaseUri(plane), outsider.Token, ct);
        Assert.NotEqual(true, (await leadClient.CallToolAsync("bind_machine", new Dictionary<string, object?>
        {
            ["machineId"] = machine.ToString(),
        }, cancellationToken: ct)).IsError);

        var refused = await leadClient.CallToolAsync("open_lead_forward", new Dictionary<string, object?>
        {
            ["serviceName"] = "secret-db",
        }, cancellationToken: ct);

        Assert.Equal(true, refused.IsError);
        // Reads as not-registered — no cross-Team tell that the name exists (§8.2).
        Assert.Contains("no service 'secret-db'", ErrorText(refused), StringComparison.Ordinal);

        await plane.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Bind_machine_refuses_a_machine_that_is_not_enrolled()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var lead = await RelayGrantTestKit.LeadSessionAsync(pg, TeamId.New(), ct);

        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, relayValidationBearer: null);
        await plane.StartAsync(ct);

        await using var leadClient = await RelayGrantTestKit.ConnectMcpAsync(
            RelayGrantTestKit.BaseUri(plane), lead.Token, ct);

        var unknown = await leadClient.CallToolAsync("bind_machine", new Dictionary<string, object?>
        {
            ["machineId"] = Guid.NewGuid().ToString(),
        }, cancellationToken: ct);
        Assert.Equal(true, unknown.IsError);
        Assert.Contains("no enrolled machine", ErrorText(unknown), StringComparison.Ordinal);

        var garbage = await leadClient.CallToolAsync("bind_machine", new Dictionary<string, object?>
        {
            ["machineId"] = "not-a-uuid",
        }, cancellationToken: ct);
        Assert.Equal(true, garbage.IsError);
        Assert.Contains("not a valid machine id", ErrorText(garbage), StringComparison.Ordinal);

        await plane.StopAsync(ct);
    }

    [SkippableFact]
    public async Task A_worker_cannot_bind_a_machine_or_open_a_lead_forward()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var team = TeamId.New();
        await RelayGrantTestKit.RegisterWorkingServiceAsync(pg, team, "db", ct);
        var workerToken = await RelayGrantTestKit.SeedWorkingWorkerTokenAsync(pg, team, ct);
        var machine = await RelayGrantTestKit.EnrollMachineAsync(pg, "some-box", ct);

        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, relayValidationBearer: null);
        await plane.StartAsync(ct);

        await using var worker = await RelayGrantTestKit.ConnectMcpAsync(
            RelayGrantTestKit.BaseUri(plane), workerToken, ct);

        // A worker credential carries no lead claim, so the whole human path is shut
        // to it — authority is structural, not a check on which tools exist (§5).
        Assert.Equal(true, (await worker.CallToolAsync("bind_machine", new Dictionary<string, object?>
        {
            ["machineId"] = machine.ToString(),
        }, cancellationToken: ct)).IsError);
        Assert.Equal(true, (await worker.CallToolAsync("open_lead_forward", new Dictionary<string, object?>
        {
            ["serviceName"] = "db",
        }, cancellationToken: ct)).IsError);
        Assert.Equal(true, (await worker.CallToolAsync(
            "unbind_machine", new Dictionary<string, object?>(), cancellationToken: ct)).IsError);

        await plane.StopAsync(ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>A typed tool return: structured content where the SDK supports it, else its JSON text block.</summary>
    private static string Payload(CallToolResult result) =>
        result.StructuredContent is { } structured
            ? structured.GetRawText()
            : string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static string ErrorText(CallToolResult result) => Text(result);

    /// <summary>No binding on the team-state view: the field is null, or omitted
    /// entirely by a null-ignoring serializer policy — both mean "bind a machine".</summary>
    private static void AssertNoBoundMachine(JsonElement teamState) =>
        Assert.True(
            !teamState.TryGetProperty("boundMachine", out var bound) || bound.ValueKind == JsonValueKind.Null,
            "team state reported a bound machine when none was bound");

    /// <summary>The §12 JSON twin, with an operator session cookie (a live human token).</summary>
    private static async Task<string> GetDashboardJsonAsync(
        WebApplication plane, string path, string humanToken, CancellationToken ct)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = RelayGrantTestKit.BaseUri(plane),
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{DashboardAuth.CookieName}={humanToken}");
        var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0)
                throw new InvalidOperationException($"stream closed after {offset}/{count} bytes");
            offset += read;
        }
        return buffer;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(50);
        }
        return condition();
    }
}
