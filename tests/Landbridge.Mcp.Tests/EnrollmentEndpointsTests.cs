using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// The machine bootstrap surface over real HTTP (spec §5 Bootstrap, §13): the
/// real <see cref="EnrollmentEndpoints"/> hosted on loopback Kestrel against a
/// Postgres-backed <see cref="TokenService"/>. A human-issued enrollment token
/// is exchanged at <c>/enroll</c> for machine credentials whose access token
/// authenticates as the enrolled machine; <c>/machine/refresh</c> mints a fresh
/// access token; and every dead/used/expired/wrong-class token — and a revoked
/// machine — is refused with the same generic 401.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EnrollmentEndpointsTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Enroll_mints_credentials_whose_access_token_authenticates_as_the_machine()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-29T00:00:00Z"));

        await using var app = BuildServer(clock);
        await app.StartAsync(ct);
        using var client = Client(app);

        var enrollment = await IssueEnrollmentAsync(clock, ct);
        var resp = await PostEnrollAsync(client, enrollment, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await ReadEnrollAsync(resp, ct);
        Assert.True(HaikuSlug.IsWellFormed(body.MachineId));
        Assert.StartsWith("lbr_m_", body.AccessToken);
        Assert.StartsWith("lbr_r_", body.RefreshToken);
        // Expiries reflect the server-side TTLs measured from the (fake) mint instant.
        Assert.Equal(clock.GetUtcNow() + TokenService.MachineAccessTtl, body.AccessExpiresAt);
        Assert.Equal(clock.GetUtcNow() + TokenService.MachineRefreshTtl, body.RefreshExpiresAt);

        await using var db = pg.NewContext();
        var tokens = new TokenService(db, clock);
        var principal = await tokens.ValidateAsync(body.AccessToken, ct);
        var machine = Assert.IsType<Principal.Machine>(principal);
        var enrolledId = await db.Set<MachineRow>().AsNoTracking()
            .Where(m => m.Slug == body.MachineId)
            .Select(m => m.Id)
            .SingleAsync(ct);
        Assert.Equal(enrolledId, machine.MachineId);
        // The refresh token authenticates nothing on its own (§5): it only mints.
        Assert.Null(await tokens.ValidateAsync(body.RefreshToken, ct));

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Enroll_refuses_used_expired_garbage_and_wrong_class_tokens_with_a_generic_401()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-29T00:00:00Z"));

        await using var app = BuildServer(clock);
        await app.StartAsync(ct);
        using var client = Client(app);

        // Used: a first enroll consumes the token; the second presentation is refused.
        var used = await IssueEnrollmentAsync(clock, ct);
        Assert.Equal(HttpStatusCode.OK, (await PostEnrollAsync(client, used, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostEnrollAsync(client, used, ct)).StatusCode);

        // Expired: past the 15-minute enrollment TTL.
        var expired = await IssueEnrollmentAsync(clock, ct);
        clock.Advance(TokenService.EnrollmentTtl + TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostEnrollAsync(client, expired, ct)).StatusCode);

        // Garbage: a well-formed-looking token that was never issued.
        var garbage = $"lbr_e_{RandomNumberGenerator.GetHexString(64)}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostEnrollAsync(client, garbage, ct)).StatusCode);

        // Wrong class: a real credential of another kind is not an enrollment token.
        string worker;
        await using (var db = pg.NewContext())
            worker = (await new TokenService(db, clock).MintWorkerTokenAsync(
                TeamId.New(), SessionId.New(), WorkerInstanceId.New(), ct)).Token;
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostEnrollAsync(client, worker, ct)).StatusCode);

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Enroll_rejects_a_malformed_body_with_400()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-29T00:00:00Z"));

        await using var app = BuildServer(clock);
        await app.StartAsync(ct);
        using var client = Client(app);

        // Missing name/os is the caller's own bug — a 400,
        // distinct from a token refusal, and no oracle for the token's validity.
        var resp = await client.PostAsJsonAsync("/enroll", new { enrollmentToken = "lbr_e_whatever" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Refresh_mints_a_working_access_token_from_a_live_refresh()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-29T00:00:00Z"));

        await using var app = BuildServer(clock);
        await app.StartAsync(ct);
        using var client = Client(app);

        var enrolled = await ReadEnrollAsync(await PostEnrollAsync(client, await IssueEnrollmentAsync(clock, ct), ct), ct);

        var refreshResp = await client.PostAsJsonAsync("/machine/refresh", new { refreshToken = enrolled.RefreshToken }, ct);
        Assert.Equal(HttpStatusCode.OK, refreshResp.StatusCode);

        using var doc = JsonDocument.Parse(await refreshResp.Content.ReadAsStringAsync(ct));
        var newAccess = doc.RootElement.GetProperty("accessToken").GetString()!;
        var newExpiry = doc.RootElement.GetProperty("accessExpiresAt").GetDateTimeOffset();

        Assert.NotEqual(enrolled.AccessToken, newAccess); // a distinct, freshly-minted token
        Assert.Equal(clock.GetUtcNow() + TokenService.MachineAccessTtl, newExpiry);

        await using var db = pg.NewContext();
        var principal = await new TokenService(db, clock).ValidateAsync(newAccess, ct);
        Assert.Equal(
            await db.Set<MachineRow>().AsNoTracking().Where(m => m.Slug == enrolled.MachineId).Select(m => m.Id).SingleAsync(ct),
            Assert.IsType<Principal.Machine>(principal).MachineId);

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Refresh_of_a_revoked_machine_is_refused_401()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-29T00:00:00Z"));

        await using var app = BuildServer(clock);
        await app.StartAsync(ct);
        using var client = Client(app);

        var enrolled = await ReadEnrollAsync(await PostEnrollAsync(client, await IssueEnrollmentAsync(clock, ct), ct), ct);

        // Un-trusting the machine (§5) kills its refresh: it mints nothing.
        await using (var db = pg.NewContext())
            await new TokenService(db, clock).RevokeMachineCredentialsAsync(
                await db.Set<MachineRow>().AsNoTracking().Where(m => m.Slug == enrolled.MachineId).Select(m => m.Id).SingleAsync(ct),
                ct);

        var refreshResp = await client.PostAsJsonAsync("/machine/refresh", new { refreshToken = enrolled.RefreshToken }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshResp.StatusCode);

        // A missing refresh token is a malformed request, not a token verdict.
        var bad = await client.PostAsJsonAsync("/machine/refresh", new { }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        await app.StopAsync(ct);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private sealed record Enrolled(
        string MachineId, string AccessToken, DateTimeOffset AccessExpiresAt,
        string RefreshToken, DateTimeOffset RefreshExpiresAt);

    private async Task<string> IssueEnrollmentAsync(TimeProvider clock, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        return (await new TokenService(db, clock).IssueEnrollmentTokenAsync(ct)).Token;
    }

    private static async Task<HttpResponseMessage> PostEnrollAsync(
        HttpClient client, string enrollmentToken, CancellationToken ct) =>
        await client.PostAsJsonAsync("/enroll", new
        {
            enrollmentToken,
            name = "box-1",
            os = "macOS 15.0",
        }, ct);

    private static async Task<Enrolled> ReadEnrollAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var r = doc.RootElement;
        return new Enrolled(
            r.GetProperty("machineId").GetString()!,
            r.GetProperty("accessToken").GetString()!,
            r.GetProperty("accessExpiresAt").GetDateTimeOffset(),
            r.GetProperty("refreshToken").GetString()!,
            r.GetProperty("refreshExpiresAt").GetDateTimeOffset());
    }

    private static HttpClient Client(WebApplication app) =>
        new() { BaseAddress = new Uri(app.Urls.First(u => u.StartsWith("http://"))) };

    private WebApplication BuildServer(TimeProvider clock)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddDbContext<LandbridgeDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddScoped<TokenService>();
        builder.Services.AddSingleton(clock);

        var app = builder.Build();
        // Anonymous endpoints: the token in the body is the credential (§5 Bootstrap).
        app.MapEnrollmentEndpoints();
        return app;
    }
}
