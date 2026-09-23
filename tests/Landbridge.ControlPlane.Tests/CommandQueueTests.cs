using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
            new CommandPayload(InstanceId: instance.Value),
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

    /// <summary>
    /// Two identical requests are two commands. They were one: the key was a hash of the
    /// payload, so a Lead answering "yes" to a session and answering "yes" again later
    /// had the second discarded and was handed the first one's outcome — permanently,
    /// and with nothing reporting the loss.
    /// </summary>
    [SkippableFact]
    public async Task An_unkeyed_repeat_is_its_own_command()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var (queue, team, session) = await SeededQueueAsync(db);
        var lead = Guid.NewGuid();

        var first = await queue.EnqueueAsync(
            CommandRow.LeadActor, lead, team, session,
            CommandRow.InputResponse, new CommandPayload(Answer: "yes"), default);
        var second = await queue.EnqueueAsync(
            CommandRow.LeadActor, lead, team, session,
            CommandRow.InputResponse, new CommandPayload(Answer: "yes"), default);

        Assert.NotEqual(first.Id, second.Id);
    }

    /// <summary>A retry names the attempt, and attaches to what was already accepted.</summary>
    [SkippableFact]
    public async Task The_same_key_twice_is_one_command()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var (queue, team, session) = await SeededQueueAsync(db);
        var lead = Guid.NewGuid();

        var first = await queue.EnqueueAsync(
            CommandRow.LeadActor, lead, team, session,
            CommandRow.StopSession, new CommandPayload(TtlSeconds: 30), default, "attempt-1");
        var retry = await queue.EnqueueAsync(
            CommandRow.LeadActor, lead, team, session,
            CommandRow.StopSession, new CommandPayload(TtlSeconds: 30), default, "attempt-1");

        Assert.Equal(first.Id, retry.Id);
    }

    /// <summary>
    /// Sameness is the caller's claim, not ours to infer: identical payloads under
    /// different keys are different requests.
    /// </summary>
    [SkippableFact]
    public async Task Different_keys_over_the_same_payload_are_different_commands()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var (queue, team, session) = await SeededQueueAsync(db);
        var lead = Guid.NewGuid();

        var first = await queue.EnqueueAsync(
            CommandRow.LeadActor, lead, team, session,
            CommandRow.Ask, new CommandPayload(Text: "which db?"), default, "attempt-1");
        var second = await queue.EnqueueAsync(
            CommandRow.LeadActor, lead, team, session,
            CommandRow.Ask, new CommandPayload(Text: "which db?"), default, "attempt-2");

        Assert.NotEqual(first.Id, second.Id);
    }

    /// <summary>A queue over <paramref name="db"/> with one session already created.</summary>
    private static async Task<(CommandQueue Queue, Guid Team, Guid Session)> SeededQueueAsync(
        LandbridgeDbContext db)
    {
        var team = TeamId.New();
        var clock = new FakeTimeProvider();
        var created = Assert.IsType<StoreResult.Applied>(await new SessionStore(db, clock).CreateAsync(
            new CreateSession(new LeadClaim(team), team, "brief", "default")));
        return (new CommandQueue(db, clock), team.Value, created.Session.Id.Value);
    }

    /// <summary>
    /// With no Core draining, the wait gives up on its own clock and answers with the
    /// command as it stands rather than holding the caller until their request times out.
    ///
    /// <para>That inversion is the point: the queue exists so a façade can accept work
    /// Core cannot currently apply, and an unbounded wait turns every mutation into a
    /// full-timeout hang exactly when Core is down. The row is durable once enqueued, so
    /// "accepted, here is the id" is both true and pollable.</para>
    /// </summary>
    [SkippableFact]
    public async Task The_wait_gives_up_on_its_own_clock_when_nothing_drains()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var (_, team, session) = await SeededQueueAsync(db);
        var queue = new CommandQueue(db, new FakeTimeProvider(), Config(("Landbridge:WriteQueueWaitMs", "200")));

        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var row = await queue.EnqueueAndWaitAsync(
            CommandRow.LeadActor, Guid.NewGuid(), team, session,
            CommandRow.StopSession, new CommandPayload(TtlSeconds: 30), caller.Token);

        Assert.Equal(CommandRow.Queued, row.Status);
        Assert.False(caller.IsCancellationRequested, "the caller's own token should be untouched");
    }

    /// <summary>The caller aborting is still an error — only the budget is a soft answer.</summary>
    [SkippableFact]
    public async Task A_cancelled_caller_still_throws()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var (_, team, session) = await SeededQueueAsync(db);
        var queue = new CommandQueue(db, new FakeTimeProvider(), Config(("Landbridge:WriteQueueWaitMs", "30000")));

        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.EnqueueAndWaitAsync(
            CommandRow.LeadActor, Guid.NewGuid(), team, session,
            CommandRow.StopSession, new CommandPayload(TtlSeconds: 30), caller.Token));
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}
