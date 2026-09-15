using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Landbridge.Contracts;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Landbridge.Hub;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Landbridge.Hub.Tests;

[Collection(PostgresCollection.Name)]
public sealed class HubGetTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = HubEndpoints.Json;

    [SkippableFact]
    public async Task Session_get_returns_the_row_by_id_and_slug()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(Patience);
        var ct = cts.Token;

        Guid sessionId;
        string slug;
        await using (var db = pg.NewContext())
        {
            var store = new SessionStore(db, new FakeTimeProvider());
            var team = TeamId.New();
            var applied = Assert.IsType<StoreResult.Applied>(await store.CreateAsync(
                new CreateSession(new LeadClaim(team), team, "brief", "default"), ct));
            sessionId = applied.Session.Id.Value;
            slug = await db.Sessions.Where(s => s.Id == sessionId).Select(s => s.Slug).SingleAsync(ct);
        }

        await using var app = HubTestHost.Build(pg.ConnectionString);
        await app.StartAsync(ct);
        using var client = HubTestHost.Client(app);

        using var byId = await client.GetAsync($"/sessions/{sessionId}", ct);
        Assert.Equal(HttpStatusCode.OK, byId.StatusCode);
        var doc = await byId.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        Assert.Equal(sessionId, doc.GetProperty("id").GetGuid());
        Assert.Equal("brief", doc.GetProperty("description").GetString());
        Assert.Equal(slug, doc.GetProperty("slug").GetString());

        using var bySlug = await client.GetAsync($"/sessions/{slug}", ct);
        Assert.Equal(HttpStatusCode.OK, bySlug.StatusCode);
        var listed = await client.GetFromJsonAsync<JsonElement>("/sessions", Json, ct);
        Assert.Equal(JsonValueKind.Array, listed.ValueKind);
        Assert.Equal(1, listed.GetArrayLength());
        Assert.Equal(sessionId, listed[0].GetProperty("id").GetGuid());

        using var log = await client.GetAsync($"/sessions/{slug}/log", ct);
        Assert.Equal(HttpStatusCode.OK, log.StatusCode);
        var events = await log.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        Assert.True(events.GetArrayLength() >= 1);

        using var exchange = await client.GetAsync($"/sessions/{sessionId}/exchange", ct);
        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);

        using var missing = await client.GetAsync($"/sessions/{Guid.NewGuid()}", ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Machine_preview_service_and_forward_gets_are_postgres_twins()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(Patience);
        var ct = cts.Token;
        var clock = TimeProvider.System;

        Guid sessionId;
        Guid teamId;
        Guid machineId;
        Guid previewId;
        Guid forwardId;
        Guid processId;
        string machineSlug;
        await using (var db = pg.NewContext())
        {
            var store = new SessionStore(db, clock);
            var team = TeamId.New();
            teamId = team.Value;
            var applied = Assert.IsType<StoreResult.Applied>(await store.CreateAsync(
                new CreateSession(new LeadClaim(team), team, "svc", "default"), ct));
            sessionId = applied.Session.Id.Value;

            db.RegisteredServices.Add(new RegisteredServiceRow
            {
                SessionId = sessionId,
                TeamId = teamId,
                Name = "web",
                Port = 8080,
                CreatedAt = clock.GetUtcNow(),
            });

            var mint = await new PreviewMappingService(db, clock)
                .CreateAsync(team, new SessionId(sessionId), "web", PreviewAuthPolicy.Gated, TimeSpan.FromMinutes(5), ct);
            previewId = mint.Mapping.Id;

            forwardId = Guid.NewGuid();
            db.Set<RelayGrantRow>().Add(new RelayGrantRow
            {
                Id = Guid.NewGuid(),
                GrantHash = "hash-not-for-get",
                ForwardId = forwardId,
                ProducerSessionId = sessionId,
                TeamId = teamId,
                ServiceName = "web",
                CreatedAt = clock.GetUtcNow(),
                ExpiresAt = clock.GetUtcNow().AddMinutes(2),
            });

            var tokens = new TokenService(db, clock);
            var enrollment = await tokens.IssueEnrollmentTokenAsync();
            var credentials = await tokens.ExchangeEnrollmentAsync(
                enrollment.Token, new MachineDeclaration("box", "linux"));
            machineId = credentials!.MachineId;
            await HubOutbox.WriteHeartbeatAsync(
                db, clock, machineId.ToString(),
                new MachineHeartbeat(
                    machineId.ToString(), Ready: true, UnderBackPressure: false,
                    default, 0, ["any-linux"], clock.GetUtcNow(),
                    Processes:
                    [
                        new ProcessStatus(
                            "dev", ProcessState.Running,
                            sessionId, clock.GetUtcNow(), null, null, false),
                    ]),
                ct);
            machineSlug = await db.Machines.Where(m => m.Id == machineId).Select(m => m.Slug).SingleAsync(ct);
            processId = await db.MachineProcesses.Where(p => p.MachineId == machineId).Select(p => p.Id).SingleAsync(ct);

            db.FrictionReports.Add(new FrictionReportRow
            {
                At = clock.GetUtcNow(),
                Role = FrictionReportRow.LeadRole,
                TeamId = teamId,
                Message = "inbox hid a report",
            });
            await db.SaveChangesAsync(ct);
        }

        await using var app = HubTestHost.Build(pg.ConnectionString);
        await app.StartAsync(ct);
        using var client = HubTestHost.Client(app);

        var machine = await client.GetFromJsonAsync<JsonElement>($"/machines/{machineSlug}", Json, ct);
        Assert.Equal(machineId, machine.GetProperty("id").GetGuid());
        Assert.True(machine.GetProperty("live").GetBoolean());
        Assert.Equal("box", machine.GetProperty("name").GetString());
        Assert.Equal(1, machine.GetProperty("processes").GetArrayLength());
        Assert.False(machine.TryGetProperty("grantHash", out _));

        var processes = await client.GetFromJsonAsync<JsonElement>($"/machines/{machineId}/processes", Json, ct);
        Assert.Equal(1, processes.GetArrayLength());
        using var process = await client.GetAsync($"/processes/{processId}", ct);
        Assert.Equal(HttpStatusCode.OK, process.StatusCode);

        var preview = await client.GetFromJsonAsync<JsonElement>($"/previews/{previewId}", Json, ct);
        Assert.Equal("web", preview.GetProperty("serviceName").GetString());
        Assert.False(preview.TryGetProperty("labelHash", out _));
        Assert.True(preview.TryGetProperty("label", out _));

        var forward = await client.GetFromJsonAsync<JsonElement>($"/forwards/{forwardId}", Json, ct);
        Assert.Equal("web", forward.GetProperty("serviceName").GetString());
        Assert.False(forward.TryGetProperty("grantHash", out _));

        var services = await client.GetFromJsonAsync<JsonElement>($"/sessions/{sessionId}/services", Json, ct);
        Assert.Equal(1, services.GetArrayLength());
        Assert.Equal("web", services[0].GetProperty("name").GetString());

        var friction = await client.GetFromJsonAsync<JsonElement>("/friction", Json, ct);
        Assert.Equal(1, friction.GetArrayLength());
        Assert.Equal("inbox hid a report", friction[0].GetProperty("message").GetString());

        using var noCreds = await client.GetAsync("/credentials", ct);
        Assert.Equal(HttpStatusCode.NotFound, noCreds.StatusCode);

        await app.StopAsync(ct);
    }
}
