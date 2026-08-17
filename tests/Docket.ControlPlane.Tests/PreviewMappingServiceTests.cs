using System.Text.RegularExpressions;
using Docket.Core;
using Microsoft.Extensions.Time.Testing;

namespace Docket.ControlPlane.Tests;

/// <summary>
/// The preview mapping store (spec §8.4): mint an opaque label, resolve it by
/// hash, and honour the mapping's own TTL (distinct from a grant's open-handshake
/// TTL). Backed by the same ephemeral Postgres fixture as the rest of the store,
/// so it exercises the real schema (the AddPreviewMappings migration) end to end.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PreviewMappingServiceTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly TeamId Team = TeamId.New();

    [SkippableFact]
    public async Task Create_then_resolve_returns_the_mapping()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var svc = new PreviewMappingService(db, clock);
        var task = SessionId.New();

        var mint = await svc.CreateAsync(Team, task, "web", PreviewAuthPolicy.Gated, TimeSpan.FromMinutes(30));

        var resolved = Assert.IsType<PreviewResolveResult.Found>(await svc.ResolveAsync(mint.Label));
        Assert.Equal(Team.Value, resolved.Mapping.TeamId);
        Assert.Equal(task.Value, resolved.Mapping.SessionId);
        Assert.Equal("web", resolved.Mapping.ServiceName);
        Assert.Equal(PreviewAuthPolicy.Gated, resolved.Mapping.AuthPolicy);
    }

    [SkippableFact]
    public async Task Resolve_unknown_label_is_not_found()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var svc = new PreviewMappingService(db, new FakeTimeProvider());

        Assert.IsType<PreviewResolveResult.NotFound>(await svc.ResolveAsync("deadbeefdeadbeefdeadbeefdeadbeef"));
        // A blank label never resolves either (a bare apex host has no label).
        Assert.IsType<PreviewResolveResult.NotFound>(await svc.ResolveAsync(""));
    }

    [SkippableFact]
    public async Task Resolve_past_the_ttl_is_expired()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var svc = new PreviewMappingService(db, clock);

        var mint = await svc.CreateAsync(Team, SessionId.New(), "web", PreviewAuthPolicy.Public, TimeSpan.FromMinutes(5));

        // Still live just before expiry.
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.IsType<PreviewResolveResult.Found>(await svc.ResolveAsync(mint.Label));

        // Expired once the TTL elapses — the mapping admits no new connections (§8.4).
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.IsType<PreviewResolveResult.Expired>(await svc.ResolveAsync(mint.Label));
    }

    [SkippableFact]
    public async Task Labels_are_opaque_dns_safe_and_distinct()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var svc = new PreviewMappingService(db, new FakeTimeProvider());

        var a = await svc.CreateAsync(Team, SessionId.New(), "web", PreviewAuthPolicy.Public, TimeSpan.FromMinutes(5));
        var b = await svc.CreateAsync(Team, SessionId.New(), "web", PreviewAuthPolicy.Public, TimeSpan.FromMinutes(5));

        Assert.NotEqual(a.Label, b.Label);
        foreach (var label in new[] { a.Label, b.Label })
        {
            // Fits one DNS label and encodes no structure — never service-task-team (§8.4).
            Assert.True(label.Length <= 63);
            Assert.Matches(new Regex("^[a-z0-9]+$"), label);
            Assert.DoesNotContain("web", label);
        }
    }

    [SkippableFact]
    public async Task Resolve_is_case_insensitive()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var svc = new PreviewMappingService(db, new FakeTimeProvider());

        var mint = await svc.CreateAsync(Team, SessionId.New(), "web", PreviewAuthPolicy.Public, TimeSpan.FromMinutes(5));

        // A browser/DNS may upcase the host label; resolution must still match (§8.4).
        Assert.IsType<PreviewResolveResult.Found>(await svc.ResolveAsync(mint.Label.ToUpperInvariant()));
    }
}
