using Docket.Core;
using Microsoft.Extensions.Time.Testing;

namespace Docket.ControlPlane.Tests;

[Collection(PostgresCollection.Name)]
public sealed class TaskEventListenerTests(PostgresFixture pg) : IAsyncLifetime
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

        await using var listener = new TaskEventListener(pg.ConnectionString);
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
        var store = new TaskStore(db, new FakeTimeProvider());
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(Team), Team, "criteria", CompletionMode.Automated, null, true), cts.Token);

        var notified = await received.Task.WaitAsync(cts.Token);
        Assert.Equal(created.Task.Id.Value, notified);

        await cts.CancelAsync();
        try { await listening; } catch (OperationCanceledException) { }
    }
}
