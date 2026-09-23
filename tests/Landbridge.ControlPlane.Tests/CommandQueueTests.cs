using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Landbridge.ControlPlane.Tests;

[Collection(PostgresCollection.Name)]
public sealed class CommandQueueTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Drain_applies_a_queued_create_session()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var team = TeamId.New();
        var session = SessionId.New();
        var clock = new FakeTimeProvider();
        await using var db = pg.NewContext();
        var queue = new CommandQueue(db, clock);
        var actor = Guid.NewGuid();
        var accepted = await queue.EnqueueAsync(
            CommandRow.LeadActor, actor, team.Value, session.Value, CommandRow.CreateSession,
            new CommandPayload("pnpm test", "default"), CancellationToken.None);
        Assert.Equal(CommandRow.Queued, accepted.Status);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(LandbridgeDbContext.BuildOptions(pg.ConnectionString));
        services.AddScoped(sp => new LandbridgeDbContext(sp.GetRequiredService<DbContextOptions<LandbridgeDbContext>>()));
        services.AddSingleton(new RunnerConnectionRegistry(clock));
        services.AddLandbridgeStore();
        await using var provider = services.BuildServiceProvider();
        var drain = new CommandDrain(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CommandDrain>.Instance);
        Assert.True(await drain.DrainOneAsync(CancellationToken.None));

        await using var check = pg.NewContext();
        var row = await check.Commands.AsNoTracking().SingleAsync(c => c.Id == accepted.Id);
        Assert.Equal(CommandRow.Applied, row.Status);
        Assert.NotNull(row.Slug);
        Assert.True(await check.Sessions.AnyAsync(s => s.Id == session.Value));
    }

    [SkippableFact]
    public async Task Drain_pull_receipt_delivers_once()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var team = TeamId.New();
        var clock = new FakeTimeProvider();
        await using var setup = pg.NewContext();
        var store = new SessionStore(setup, clock);
        var created = Assert.IsType<StoreResult.Applied>(await store.CreateAsync(
            new CreateSession(new LeadClaim(team), team, "brief", "default")));
        var instance = WorkerInstanceId.New();
        Assert.IsType<StoreResult.Applied>(await store.DispatchNextAsync(
            new MachineSnapshot(Guid.NewGuid(), true, false, new HashSet<string> { "default" }), instance));
        var caller = new WorkerCaller(team, created.Session.Id, instance);
        Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(
            created.Session.Id, new RequestInput(caller, InputRequestKind.Question, "which db?")));
        Assert.IsType<StoreResult.Applied>(await store.AnswerOrWakeAsync(
            new LeadClaim(team), created.Session.Id, null, "postgres", sessionLive: true));

        var queue = new CommandQueue(setup, clock);
        var accepted = await queue.EnqueueAsync(
            CommandRow.WorkerActor, created.Session.Id.Value, team.Value, created.Session.Id.Value,
            CommandRow.PullReceipt,
            new CommandPayload(InstanceId: instance.Value, Nonce: Guid.NewGuid()),
            CancellationToken.None);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(LandbridgeDbContext.BuildOptions(pg.ConnectionString));
        services.AddScoped(sp => new LandbridgeDbContext(sp.GetRequiredService<DbContextOptions<LandbridgeDbContext>>()));
        services.AddSingleton(new RunnerConnectionRegistry(clock));
        services.AddLandbridgeStore();
        await using var provider = services.BuildServiceProvider();
        Assert.True(await new CommandDrain(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CommandDrain>.Instance).DrainOneAsync(CancellationToken.None));

        await using var check = pg.NewContext();
        var row = await check.Commands.AsNoTracking().SingleAsync(c => c.Id == accepted.Id);
        Assert.Equal(CommandRow.Applied, row.Status);
        var read = new SessionStore(check, clock);
        var delivered = await read.ReadWorkerInboxAsync(caller, delivered: true);
        Assert.Equal("postgres", Assert.Single(delivered!.Items).Text);
        var again = await read.ReadWorkerInboxAsync(caller, delivered: false);
        Assert.Empty(again!.Items);
        Assert.Equal(MessageState.Idle, (await check.Sessions.AsNoTracking().SingleAsync(s => s.Id == created.Session.Id.Value)).MessageState);
    }
}
