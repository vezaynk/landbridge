using Landbridge.Core;
using Microsoft.Extensions.Time.Testing;

namespace Landbridge.ControlPlane.Tests;

[Collection(PostgresCollection.Name)]
public sealed class SessionEventListenerTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly TeamId Team = TeamId.New();

    [SkippableFact]
    public async Task A_committed_transition_notifies_the_listener_with_the_task_id()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);

        await using var listener = new SessionEventListener(pg.ConnectionString);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var received = new TaskCompletionSource<Guid>();
        var listening = Task.Run(async () =>
        {
            await foreach (var id in listener.ListenAsync(cts.Token))
            {
                received.TrySetResult(id);
                return;
            }
        });

        // Give LISTEN a moment to register before we NOTIFY.
        await Task.Delay(500, cts.Token);

        await using var db = pg.NewContext();
        var store = new SessionStore(db, new FakeTimeProvider());
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateSession(new LeadClaim(Team), Team, "criteria", "default"), cts.Token);

        var notified = await received.Task.WaitAsync(cts.Token);
        Assert.Equal(created.Session.Id.Value, notified);

        await cts.CancelAsync();
        try { await listening; } catch (OperationCanceledException) { }
    }
}
