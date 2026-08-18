using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Landbridge.Relay;
using Microsoft.Extensions.DependencyInjection;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// The real relay-side grant validator (spec §8.3) against a real control plane:
/// the relay's <see cref="ControlPlaneGrantValidator"/> asks the plane's
/// <c>/relay/validate</c> endpoint over HTTP, authenticated by the shared bearer.
/// Covers a real issued grant validating true once per role, a garbage grant
/// validating false, fail-closed when the plane is unreachable, and — the crown —
/// two real <see cref="System.Net.WebSockets.ClientWebSocket"/> ends splicing
/// through the relay on a grant the plane actually blessed.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ControlPlaneGrantValidatorTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string Bearer = "relay-shared-secret-under-test";

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Real_grant_validates_true_once_per_role_and_garbage_false()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var team = TeamId.New();
        await RelayGrantTestKit.RegisterWorkingServiceAsync(pg, team, "db", ct);
        var issued = await RelayGrantTestKit.IssueGrantAsync(pg, team, "db", ct);

        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, Bearer);
        await plane.StartAsync(ct);
        await using var relay = RelayGrantTestKit.BuildRelay(RelayGrantTestKit.BaseUri(plane).ToString(), Bearer);
        await relay.StartAsync(ct);

        var validator = relay.Services.GetRequiredService<IGrantValidator>();
        Assert.IsType<ControlPlaneGrantValidator>(validator);

        var forwardId = issued.ForwardId.ToString();
        // Each end opens once.
        Assert.True(await validator.ValidateAsync(issued.Grant, forwardId, RelayRole.Consumer, ct));
        Assert.True(await validator.ValidateAsync(issued.Grant, forwardId, RelayRole.Producer, ct));
        // A replay of either end is refused (single-use per role, §8.3).
        Assert.False(await validator.ValidateAsync(issued.Grant, forwardId, RelayRole.Consumer, ct));
        // A grant the plane never issued is refused.
        Assert.False(await validator.ValidateAsync("lbr_g_garbage", forwardId, RelayRole.Consumer, ct));

        await relay.StopAsync(ct);
        await plane.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Unreachable_control_plane_fails_closed()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var ct = cts.Token;

        // No plane at this address: the validator must refuse rather than accept
        // on doubt (§8.3, §13). Port 1 is never bound — connect refuses at once.
        await using var relay = RelayGrantTestKit.BuildRelay("http://127.0.0.1:1", Bearer);
        await relay.StartAsync(ct);

        var validator = relay.Services.GetRequiredService<IGrantValidator>();
        Assert.False(await validator.ValidateAsync("lbr_g_whatever", Guid.NewGuid().ToString(), RelayRole.Consumer, ct));

        await relay.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Validate_endpoint_fails_closed_without_a_bearer_and_refuses_a_wrong_one()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var ct = cts.Token;
        using var http = new HttpClient();

        // No shared bearer configured → the endpoint validates nothing and 503s,
        // fail-closed, whatever the body says (§8.3, §13).
        await using (var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, relayValidationBearer: null))
        {
            await plane.StartAsync(ct);
            var resp = await http.PostAsJsonAsync(
                new Uri(RelayGrantTestKit.BaseUri(plane), "relay/validate"),
                new { grant = "x", forwardId = Guid.NewGuid().ToString(), role = "consumer" }, ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            await plane.StopAsync(ct);
        }

        // Bearer configured, but a caller presenting the wrong one is refused 401
        // before any grant is looked at.
        await using (var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, Bearer))
        {
            await plane.StartAsync(ct);
            using var req = new HttpRequestMessage(
                HttpMethod.Post, new Uri(RelayGrantTestKit.BaseUri(plane), "relay/validate"))
            {
                Content = JsonContent.Create(new { grant = "x", forwardId = Guid.NewGuid().ToString(), role = "consumer" }),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "the-wrong-secret");
            var resp = await http.SendAsync(req, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            await plane.StopAsync(ct);
        }
    }

    [SkippableFact]
    public async Task Crown_two_ends_splice_through_the_relay_on_a_real_issued_grant()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var team = TeamId.New();
        await RelayGrantTestKit.RegisterWorkingServiceAsync(pg, team, "db", ct);
        var issued = await RelayGrantTestKit.IssueGrantAsync(pg, team, "db", ct);

        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, Bearer);
        await plane.StartAsync(ct);
        await using var relay = RelayGrantTestKit.BuildRelay(RelayGrantTestKit.BaseUri(plane).ToString(), Bearer);
        await relay.StartAsync(ct);

        var tunnel = RelayGrantTestKit.TunnelUri(relay);
        var forwardId = issued.ForwardId.ToString();

        // Both landbridged ends (here, plain ClientWebSockets) present the SAME real
        // grant with their own role; the relay validates each against the plane
        // before splicing.
        using var consumer = await RelayGrantTestKit.ConnectTunnelAsync(tunnel, forwardId, issued.Grant, "consumer", ct);
        using var producer = await RelayGrantTestKit.ConnectTunnelAsync(tunnel, forwardId, issued.Grant, "producer", ct);

        var toProducer = new byte[64 * 1024];
        var toConsumer = new byte[48 * 1024];
        Random.Shared.NextBytes(toProducer);
        Random.Shared.NextBytes(toConsumer);

        var sendC = RelayGrantTestKit.SendAsync(consumer, toProducer, ct);
        var sendP = RelayGrantTestKit.SendAsync(producer, toConsumer, ct);
        var gotOnProducer = RelayGrantTestKit.ReceiveExactlyAsync(producer, toProducer.Length, ct);
        var gotOnConsumer = RelayGrantTestKit.ReceiveExactlyAsync(consumer, toConsumer.Length, ct);

        await Task.WhenAll(sendC, sendP);
        var producerBytes = await gotOnProducer;
        var consumerBytes = await gotOnConsumer;
        Assert.True(toProducer.AsSpan().SequenceEqual(producerBytes), "consumer→producer bytes differ");
        Assert.True(toConsumer.AsSpan().SequenceEqual(consumerBytes), "producer→consumer bytes differ");

        await consumer.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", ct);
        await producer.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", ct);

        await relay.StopAsync(ct);
        await plane.StopAsync(ct);
    }

    // ── §9.10 byte accounting over the same plane-facing contract ─────────────

    [SkippableFact]
    public async Task Spliced_bytes_reach_the_plane_and_land_on_the_owning_team()
    {
        // The whole §9.10 path with nothing faked: a real relay counts a real splice, reports to
        // the real plane over /relay/usage, and the plane attributes the bytes to the Team that
        // owns the forward id it minted. The relay is never told whose bytes these are.
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var team = TeamId.New();
        await RelayGrantTestKit.RegisterWorkingServiceAsync(pg, team, "db", ct);
        var issued = await RelayGrantTestKit.IssueGrantAsync(pg, team, "db", ct);

        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, Bearer);
        await plane.StartAsync(ct);
        await using var relay = RelayGrantTestKit.BuildRelay(RelayGrantTestKit.BaseUri(plane).ToString(), Bearer);
        await relay.StartAsync(ct);

        var forwardId = issued.ForwardId.ToString();
        var tunnel = RelayGrantTestKit.TunnelUri(relay);
        using var consumer = await RelayGrantTestKit.ConnectTunnelAsync(tunnel, forwardId, issued.Grant, "consumer", ct);
        using var producer = await RelayGrantTestKit.ConnectTunnelAsync(tunnel, forwardId, issued.Grant, "producer", ct);

        var payload = new byte[20 * 1024];
        Random.Shared.NextBytes(payload);
        await RelayGrantTestKit.SendAsync(consumer, payload, ct);
        var got = await RelayGrantTestKit.ReceiveExactlyAsync(producer, payload.Length, ct);
        Assert.True(payload.AsSpan().SequenceEqual(got), "consumer→producer bytes differ");

        // Closing runs the on-close flush — the reason a forward shorter than one flush interval
        // is counted at all.
        await consumer.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", ct);

        await using var db = pg.NewContext();
        var usage = new TeamForwardUsageService(db, TimeProvider.System);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        TeamForwardUsageView view;
        do
        {
            db.ChangeTracker.Clear();
            view = await usage.ReadAsync(team, ct);
            if (view.ForwardedBytes >= payload.Length)
                break;
            await Task.Delay(50, ct);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.True(view.Measured, "the plane never received a byte report");
        Assert.Equal(payload.Length, view.ForwardedBytes);

        await relay.StopAsync(ct);
        await plane.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Usage_reporting_is_bearer_gated_exactly_like_validation()
    {
        // Same contract, same door. The endpoint records a number a human reads, but it is still
        // an unauthenticated caller's chance to write to the plane, so it fails closed with no
        // bearer configured and refuses a wrong one.
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var body = new { reports = new[] { new { forwardId = Guid.NewGuid().ToString(), bytes = 10L } } };

        // No bearer configured: refuse everything, loudly (503).
        await using (var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, relayValidationBearer: null))
        {
            await plane.StartAsync(ct);
            using var client = new HttpClient();
            var res = await client.PostAsJsonAsync(new Uri(RelayGrantTestKit.BaseUri(plane), "relay/usage"), body, ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
            await plane.StopAsync(ct);
        }

        await using (var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, Bearer))
        {
            await plane.StartAsync(ct);
            using var client = new HttpClient();
            var url = new Uri(RelayGrantTestKit.BaseUri(plane), "relay/usage");

            using var wrong = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(body),
            };
            wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "the-wrong-secret");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrong, ct)).StatusCode);

            // Right bearer, unknown forward id: 200 with nothing attributed — a best-effort report
            // racing a grant's lifetime is dropped, not an error, and the count says so.
            using var unknown = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(body),
            };
            unknown.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Bearer);
            var ok = await client.SendAsync(unknown, ct);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Contains("\"attributed\":0", await ok.Content.ReadAsStringAsync(ct));

            // A malformed body is a 400 rather than a silent no-op.
            using var malformed = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new { nothing = true }),
            };
            malformed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Bearer);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(malformed, ct)).StatusCode);

            await plane.StopAsync(ct);
        }
    }
}
