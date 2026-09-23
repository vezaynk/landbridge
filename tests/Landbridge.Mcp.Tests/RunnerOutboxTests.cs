using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Landbridge.Contracts;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Landbridge.Mcp.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// Outbound commands sit in <c>runner_outbox</c>. <c>GET /runner/events</c>
/// replays the unacked ones. An ack drops them from the next snapshot.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RunnerOutboxTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Events_replay_unacked_commands_until_ack()
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
        Assert.NotNull(creds);

        var sent = await app.Services.GetRequiredService<RunnerConnectionRegistry>()
            .SendAsync(creds!.MachineId, new KillCommand(SessionId.New()), ct);
        Assert.False(sent);
        await using var queued = pg.NewContext();
        var pending = await queued.RunnerOutbox.AsNoTracking().ToListAsync(ct);
        Assert.Equal(1, pending.Count(r => r.MachineId == creds.MachineId && r.AckedAt == null));

        using var client = Client(app, creds.Access.Token);
        using var first = await client.GetAsync("/runner/events", HttpCompletionOption.ResponseHeadersRead, ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var line = await ReadCommandAsync(first, ct);
        using var doc = JsonDocument.Parse(line);
        var id = doc.RootElement.GetProperty("id").GetInt64();
        Assert.Equal("kill", doc.RootElement.GetProperty("kind").GetString());
        first.Dispose();

        using var ack = await client.PostAsync($"/runner/commands/{id}/ack", null, ct);
        Assert.Equal(HttpStatusCode.NoContent, ack.StatusCode);

        using var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, quiet.Token);
        using var second = await client.GetAsync("/runner/events", HttpCompletionOption.ResponseHeadersRead, ct);
        string? again;
        try
        {
            again = await ReadCommandOrTimeoutAsync(second, linked.Token);
        }
        catch (OperationCanceledException) when (quiet.IsCancellationRequested)
        {
            again = null;
        }
        Assert.Null(again);
        await app.StopAsync(ct);
    }

    /// <summary>
    /// A command addressed to one machine does not reach another machine's stream.
    /// The fan-out is keyed by machine, so this is also what keeps one command from
    /// waking — and costing a query on — every runner in the fleet.
    /// </summary>
    [SkippableFact]
    public async Task One_machines_command_does_not_reach_anothers_stream()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;
        await using var app = BuildCore();
        await app.StartAsync(ct);
        var (a, b) = await TwoMachinesAsync(pg, ct);

        await app.Services.GetRequiredService<RunnerConnectionRegistry>()
            .SendAsync(a.MachineId, new KillCommand(SessionId.New()), ct);

        // B is listening and must stay empty.
        using var bClient = Client(app, b.Access.Token);
        using var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, quiet.Token);
        using var bStream = await bClient.GetAsync(
            "/runner/events", HttpCompletionOption.ResponseHeadersRead, ct);
        string? leaked;
        try
        {
            leaked = await ReadCommandOrTimeoutAsync(bStream, linked.Token);
        }
        catch (OperationCanceledException) when (quiet.IsCancellationRequested)
        {
            leaked = null;
        }
        Assert.Null(leaked);

        // The same command is waiting for A, so the silence above is B's, not an
        // outbox that dropped it.
        using var aClient = Client(app, a.Access.Token);
        using var aStream = await aClient.GetAsync(
            "/runner/events", HttpCompletionOption.ResponseHeadersRead, ct);
        using var doc = JsonDocument.Parse(await ReadCommandAsync(aStream, ct));
        Assert.Equal("kill", doc.RootElement.GetProperty("kind").GetString());
        await app.StopAsync(ct);
    }

    /// <summary>
    /// A stream with nothing to say still says something. Without it an idle proxy
    /// closes a quiet stream out from under both ends.
    /// </summary>
    [SkippableFact]
    public async Task A_quiet_stream_sends_a_keepalive()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;
        await using var app = BuildCore(keepAliveMs: 150);
        await app.StartAsync(ct);

        await using var db = pg.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        var creds = await tokens.ExchangeEnrollmentAsync(
            (await tokens.IssueEnrollmentTokenAsync(ct)).Token, new MachineDeclaration("idle", "linux"), ct);
        Assert.NotNull(creds);

        using var client = Client(app, creds!.Access.Token);
        using var stream = await client.GetAsync(
            "/runner/events", HttpCompletionOption.ResponseHeadersRead, ct);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.Equal(": keepalive", await ReadCommentAsync(stream, ct));
        await app.StopAsync(ct);
    }

    private static async Task<string> ReadCommandAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var found = await ReadCommandOrTimeoutAsync(response, ct);
        Assert.NotNull(found);
        return found!;
    }

    private static async Task<string?> ReadCommandOrTimeoutAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                return null;
            if (line.StartsWith("data: ", StringComparison.Ordinal))
                return line["data: ".Length..];
        }
        return null;
    }

    private static async Task<string?> ReadCommentAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        // ": open" goes out as soon as the stream is established; the keepalive is the
        // next comment after it, which is the one this is waiting for.
        var seenOpen = false;
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                return null;
            if (!line.StartsWith(':'))
                continue;
            if (!seenOpen)
            {
                seenOpen = true;
                continue;
            }
            return line;
        }
        return null;
    }

    private static async Task<(MachineCredentials A, MachineCredentials B)> TwoMachinesAsync(
        PostgresFixture pg, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        var first = await tokens.ExchangeEnrollmentAsync(
            (await tokens.IssueEnrollmentTokenAsync(ct)).Token, new MachineDeclaration("a", "linux"), ct);
        var second = await tokens.ExchangeEnrollmentAsync(
            (await tokens.IssueEnrollmentTokenAsync(ct)).Token, new MachineDeclaration("b", "linux"), ct);
        Assert.NotNull(first);
        Assert.NotNull(second);
        return (first!, second!);
    }

    private static HttpClient Client(WebApplication app, string bearer)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal))),
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    private WebApplication BuildCore(int? keepAliveMs = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["ConnectionStrings:Landbridge"] = pg.ConnectionString;
        builder.Configuration["Landbridge:PublicMcpUrl"] = "https://mcp.example.com";
        builder.Configuration["Landbridge:AuthUrl"] = "https://auth.example.com";
        if (keepAliveMs is { } ms)
            builder.Configuration["Landbridge:RunnerStreamKeepAliveMs"] = ms.ToString();
        builder.AddPlane();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRunnerEndpoint();
        return app;
    }
}
