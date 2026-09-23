using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Landbridge.Contracts;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
using Landbridge.Mcp.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// Part 3 phase 1: a machine POSTs the same §10 frame the WebSocket carries.
/// Commands still go down the socket.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RunnerIngestTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Heartbeat_post_upserts_last_spoke_without_a_socket()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;
        await using var app = BuildCore();
        await app.StartAsync(ct);

        await using var db = pg.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        var enrollment = await tokens.IssueEnrollmentTokenAsync(ct);
        var creds = await tokens.ExchangeEnrollmentAsync(enrollment.Token, new MachineDeclaration("box", "macos"), ct);
        Assert.NotNull(creds);

        var frame = RunnerWire.EncodeHeartbeat(new MachineHeartbeat(
            Ready: true, UnderBackPressure: false, new SystemLoad(0.1, 0.2, 0.3),
            RunningSessions: 0, Profiles: ["goose"], At: DateTimeOffset.UtcNow));
        using var client = new HttpClient
        {
            BaseAddress = new Uri(app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal))),
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", creds!.Access.Token);
        using var resp = await client.PostAsync("/runner/ingest",
            new StringContent(frame, Encoding.UTF8, "application/json"), ct);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        await using var check = pg.NewContext();
        var machine = await check.Machines.AsNoTracking().SingleAsync(m => m.Id == creds.MachineId, ct);
        Assert.True(machine.Ready);
        Assert.Equal(["goose"], machine.Profiles);
        Assert.NotNull(machine.LastSpokeAt);
        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task An_unrecognized_frame_is_400_and_a_lead_token_is_403()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;
        await using var app = BuildCore();
        await app.StartAsync(ct);

        await using var db = pg.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        var enrollment = await tokens.IssueEnrollmentTokenAsync(ct);
        var creds = await tokens.ExchangeEnrollmentAsync(enrollment.Token, new MachineDeclaration("box", "linux"), ct);
        using var client = new HttpClient
        {
            BaseAddress = new Uri(app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal))),
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", creds!.Access.Token);
        using var bad = await client.PostAsync("/runner/ingest",
            new StringContent("{\"type\":\"nope\"}", Encoding.UTF8, "application/json"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var human = await tokens.IssueHumanSessionAsync(ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", human.Token);
        using var refused = await client.PostAsync("/runner/ingest",
            new StringContent("{}", Encoding.UTF8, "application/json"), ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        await app.StopAsync(ct);
    }

    private WebApplication BuildCore()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["ConnectionStrings:Landbridge"] = pg.ConnectionString;
        builder.Configuration["Landbridge:PublicMcpUrl"] = "https://mcp.example.com";
        builder.Configuration["Landbridge:AuthUrl"] = "https://auth.example.com";
        builder.AddPlane();
        builder.Services.AddSingleton(new SessionEventListener(pg.ConnectionString));
        builder.Services.AddSingleton(sp => new DispatchService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<RunnerConnectionRegistry>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<DispatchService>>(),
            sp.GetRequiredService<SessionEventListener>()));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRunnerEndpoint();
        return app;
    }
}
