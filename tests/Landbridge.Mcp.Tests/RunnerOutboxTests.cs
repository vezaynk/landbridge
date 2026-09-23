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

    private static HttpClient Client(WebApplication app, string bearer)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal))),
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client;
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
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRunnerEndpoint();
        return app;
    }
}
