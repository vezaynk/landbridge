using Landbridge.Core;
using Microsoft.Extensions.Time.Testing;

namespace Landbridge.ControlPlane.Tests;

[Collection(PostgresCollection.Name)]
public sealed class SessionEventFanoutTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly TeamId Team = TeamId.New();

    [SkippableFact]
    public async Task A_committed_transition_wakes_a_subscriber()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);

        await using var fanout = new SessionEventFanout(pg.ConnectionString);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await fanout.StartAsync(cts.Token);
        await fanout.WhenListening.WaitAsync(cts.Token);

        var woke = new TaskCompletionSource();
        using var sub = fanout.Subscribe();
        var reading = Task.Run(async () =>
        {
            await foreach (var _ in sub.Reader.ReadAllAsync(cts.Token))
            {
                woke.TrySetResult();
                return;
            }
        }, cts.Token);

        await using (var db = pg.NewContext())
        {
            var store = new SessionStore(db, new FakeTimeProvider());
            Assert.IsType<StoreResult.Applied>(await store.CreateAsync(
                new CreateSession(new LeadClaim(Team), Team, "wake me", "default"), cts.Token));
        }

        await woke.Task.WaitAsync(cts.Token);

        await cts.CancelAsync();
        try { await reading; } catch (OperationCanceledException) { }
        await fanout.StopAsync(CancellationToken.None);
    }

    [SkippableFact]
    public async Task A_session_filter_does_not_wake_on_another_session()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);

        await using var fanout = new SessionEventFanout(pg.ConnectionString);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await fanout.StartAsync(cts.Token);
        await fanout.WhenListening.WaitAsync(cts.Token);

        SessionId watched;
        await using (var db = pg.NewContext())
        {
            var store = new SessionStore(db, new FakeTimeProvider());
            watched = ((StoreResult.Applied)await store.CreateAsync(
                new CreateSession(new LeadClaim(Team), Team, "watch me", "default"), cts.Token)).Session.Id;
        }

        var woke = new TaskCompletionSource();
        using var sub = fanout.Subscribe(watched.Value);
        var reading = Task.Run(async () =>
        {
            await foreach (var _ in sub.Reader.ReadAllAsync(cts.Token))
            {
                woke.TrySetResult();
                return;
            }
        }, cts.Token);

        await using (var db = pg.NewContext())
        {
            var store = new SessionStore(db, new FakeTimeProvider());
            Assert.IsType<StoreResult.Applied>(await store.CreateAsync(
                new CreateSession(new LeadClaim(Team), Team, "not you", "default"), cts.Token));
        }

        await Task.Delay(200, cts.Token);
        Assert.False(woke.Task.IsCompleted);

        await using (var db = pg.NewContext())
        {
            var store = new SessionStore(db, new FakeTimeProvider());
            Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(
                watched, new Cancel(new LeadClaim(Team), CancelDisposition.Preserve), cts.Token));
        }

        await woke.Task.WaitAsync(cts.Token);

        await cts.CancelAsync();
        try { await reading; } catch (OperationCanceledException) { }
        await fanout.StopAsync(CancellationToken.None);
    }
}
