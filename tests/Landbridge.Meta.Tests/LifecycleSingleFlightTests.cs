using Docket.Meta.Data;
using Docket.Meta.Provisioning;
using Microsoft.EntityFrameworkCore;

namespace Docket.Meta.Tests;

/// <summary>
/// The two properties that make the lifecycle transitions safe to run in the background
/// (design note §2), neither of which the shape they used to have could have:
///
/// <list type="number">
/// <item><b>An interrupted transition is visible and gets finished.</b> An upgrade removes
/// and recreates the plane containers, so it must record that it is mid-flight; otherwise
/// its ordinary failure — the plane not answering in time — leaves the row exactly as it
/// was, <c>ready</c>, with a container missing and nothing that will ever notice.</item>
/// <item><b>One transition at a time.</b> A destroy that starts while an upgrade is
/// recreating containers tears down objects the saga is about to recreate, and the check a
/// request makes before launching a background operation cannot prevent it — the operation
/// outlives the request by the whole length of the upgrade. The Instance row is what says
/// "taken", and it says so for as long as the operation runs.</item>
/// </list>
/// </summary>
public class LifecycleSingleFlightTests
{
    private static async Task<InstanceRow> ReadyInstanceAsync(SagaHarness h, string tag = "v1")
    {
        await h.AddHostAsync();
        var r = await h.Creator.CreateAsync(new CreateInstanceRequest("acme", null, tag, null), default);
        await h.Provisioner.ProvisionAsync(r.Id, default);
        return await h.Db.Instances.Include(i => i.Host).SingleAsync(i => i.Id == r.Id);
    }

    private static async Task<InstanceRow> ReadAsync(MetaDbContext db, Guid id) =>
        await db.Instances.AsNoTracking().Include(i => i.Steps).SingleAsync(i => i.Id == id);

    /// <summary>A TCS whose continuations never run on the signalling thread — no re-entrant test steps.</summary>
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The refusal a second operation must get, asserted with a bound on the wait. A guard
    /// that admitted the operation would not return an answer at all — it would park in the
    /// substrate behind the operation that already holds the row — so without the bound a
    /// regression here would hang a run instead of failing it.
    /// </summary>
    private static async Task<InstanceBusyException> RefusedAsync(Task operation)
    {
        Assert.Same(operation, await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(10))));
        return await Assert.ThrowsAsync<InstanceBusyException>(() => operation);
    }

    // ── An interrupted upgrade converges ─────────────────────────────────────────

    /// <summary>
    /// The filed defect, end to end: a failure part-way through an upgrade — after the mcp
    /// container has been replaced on the new tag, while the relay has not — must leave a
    /// state something will finish, and finishing it must actually restore the missing
    /// container. Before, the row stayed <c>ready</c> with no relay container and the
    /// reconciler selected neither ready rows nor anything else that would have caught it,
    /// so the only trace was a log warning.
    /// </summary>
    [Fact]
    public async Task A_failure_mid_upgrade_leaves_a_state_the_reconciler_finishes()
    {
        using var h = new SagaHarness();
        var i = await ReadyInstanceAsync(h);

        var failRelay = true;
        h.Substrate.BeforeOp = op =>
        {
            if (failRelay && op == "run:docket-acme-relay")
                throw new InvalidOperationException("relay would not come up");
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Provisioner.UpgradeAsync(i.Id, "v2", default));

        // The tag the operator asked for is recorded and the mcp container is already on it:
        // the upgrade is half applied.
        var stalled = await ReadAsync(h.Db, i.Id);
        Assert.Equal("v2", stalled.ImageTag);
        Assert.Equal("docket-mcp:v2", h.Substrate.Containers["docket-acme-mcp"].Spec.Image);

        // The invariant the old shape broke, stated as the conjunction it broke: it recorded
        // nothing in flight and removed BOTH plane containers before recreating either, so
        // this exact throw left the row persisted `ready` with no relay container at all.
        Assert.False(
            stalled.State is InstanceState.Ready && !h.Substrate.Containers.ContainsKey("docket-acme-relay"),
            "the row reads ready while a container is missing — a half-applied upgrade nothing will heal");

        // What it reads instead is the step it stalled at, which the sweep selects.
        Assert.Equal(InstanceState.Failed, stalled.State);
        Assert.Equal(ProvisionStep.StartRelay, stalled.FailedStep);

        // And the relay the upgrade had not reached yet is still up on the old tag rather
        // than removed ahead of time: each step now replaces only what it recreates, so a
        // stall costs one container's worth of downtime instead of the whole plane's.
        Assert.Equal("docket-relay:v1", h.Substrate.Containers["docket-acme-relay"].Spec.Image);

        // One sweep, once the stall has waited out its retry backoff, and the upgrade
        // finishes from the checkpoint it stalled at.
        failRelay = false;
        h.Clock.Advance(InstanceReconciler.FailedRetryAfter);
        Assert.Equal(1, await h.NewReconciler().SweepAsync(default));

        var healed = await ReadAsync(h.Db, i.Id);
        Assert.Equal(InstanceState.Ready, healed.State);
        Assert.Null(healed.FailedStep);
        Assert.Equal("docket-relay:v2", h.Substrate.Containers["docket-acme-relay"].Spec.Image);
        Assert.All(healed.Steps, s => Assert.Equal(StepStatus.Done, s.Status));
    }

    /// <summary>
    /// The same defect by its other route: the process died mid-upgrade rather than the
    /// upgrade failing. Nothing is holding the row and no exception was ever recorded, so
    /// the in-flight state is the only evidence — and it is enough, because the tag and the
    /// invalidated checkpoints already say what the upgrade was going to do.
    /// </summary>
    [Fact]
    public async Task An_upgrade_abandoned_mid_flight_is_taken_over_and_completed()
    {
        using var h = new SagaHarness();
        var i = await ReadyInstanceAsync(h);

        // A process that died between claiming the row and finishing the saga: the row is
        // `upgrading` on the new tag, the plane containers are still the old ones.
        var park = Gate();
        var release = Gate();
        h.Substrate.BeforeOp = async op =>
        {
            if (op != "run:docket-acme-mcp") return;
            park.TrySetResult();
            await release.Task;
        };
        var abandoned = h.Provisioner.UpgradeAsync(i.Id, "v2", default);
        await park.Task;

        Assert.Equal(InstanceState.Upgrading, (await ReadAsync(h.Db, i.Id)).State);

        // Nothing in this process holds it any more (the tracker is empty, exactly as it is
        // after a restart), so the sweep takes it over and runs the saga to the end.
        h.Substrate.BeforeOp = null;
        Assert.Equal(1, await h.NewReconciler().SweepAsync(default));

        var healed = await ReadAsync(h.Db, i.Id);
        Assert.Equal(InstanceState.Ready, healed.State);
        Assert.Equal("docket-mcp:v2", h.Substrate.Containers["docket-acme-mcp"].Spec.Image);
        Assert.Equal("docket-relay:v2", h.Substrate.Containers["docket-acme-relay"].Spec.Image);

        release.SetResult();
        await Task.WhenAny(abandoned, Task.Delay(TimeSpan.FromSeconds(10)));
    }

    /// <summary>
    /// An operation whose intent is not "be ready" is finished in its own direction, never
    /// converged upward: a suspend interrupted half-way must end suspended, not restarted.
    /// </summary>
    [Fact]
    public async Task An_abandoned_suspend_is_finished_as_a_suspend()
    {
        using var h = new SagaHarness();
        var i = await ReadyInstanceAsync(h);

        // Straight to the state an interrupted suspend leaves, since the suspend itself is
        // too short to park inside reliably.
        var row = await h.Db.Instances.SingleAsync(x => x.Id == i.Id);
        row.State = InstanceState.Suspending;
        await h.Db.SaveChangesAsync();

        Assert.Equal(1, await h.NewReconciler().SweepAsync(default));

        Assert.Equal(InstanceState.Suspended, (await ReadAsync(h.Db, i.Id)).State);
        Assert.All(h.Substrate.Containers.Values, c => Assert.False(c.Running));
        Assert.Empty(h.Caddy.Routes);
    }

    /// <summary>
    /// A stall is retried without an operator, but not on every sweep: a tag that will never
    /// pull would otherwise be re-pulled every interval forever. The backoff is what spaces
    /// the retries out, and it is measured from the last step activity, so it needs no
    /// counter on the row.
    /// </summary>
    [Fact]
    public async Task A_fresh_stall_waits_out_its_backoff_before_being_retried()
    {
        using var h = new SagaHarness();
        var i = await ReadyInstanceAsync(h);

        h.Substrate.BeforeOp = op => op == "run:docket-acme-relay"
            ? throw new InvalidOperationException("relay would not come up")
            : Task.CompletedTask;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Provisioner.UpgradeAsync(i.Id, "v2", default));

        var reconciler = h.NewReconciler();
        Assert.Equal(0, await reconciler.SweepAsync(default));

        h.Clock.Advance(InstanceReconciler.FailedRetryAfter);
        Assert.Equal(1, await reconciler.SweepAsync(default));
    }

    // ── One transition at a time ────────────────────────────────────────────────

    /// <summary>
    /// A destroy arriving while an upgrade is mid-flight is refused, and refused by the row
    /// rather than by a check the request happened to make first: the upgrade is parked
    /// inside a step here, which is exactly the window that used to be wide open — the
    /// operation had recorded nothing, so the row still read <c>ready</c> and a destroy
    /// walked straight in and removed containers the saga was about to recreate.
    /// </summary>
    [Fact]
    public async Task A_destroy_cannot_start_while_an_upgrade_holds_the_row()
    {
        using var h = new SagaHarness();
        var i = await ReadyInstanceAsync(h);

        var park = Gate();
        var release = Gate();
        h.Substrate.BeforeOp = async op =>
        {
            if (op != "run:docket-acme-mcp") return;
            park.TrySetResult();
            await release.Task;
        };

        var upgrade = h.Provisioner.UpgradeAsync(i.Id, "v2", default);
        await park.Task;
        Assert.Equal(InstanceState.Upgrading, (await ReadAsync(h.Db, i.Id)).State);

        // A second operator's request: its own context and provisioner, as a second request
        // would have, against the same store and the same host.
        using var second = h.NewStore();
        var refused = await RefusedAsync(h.NewProvisioner(second).DestroyAsync(i.Id, default));
        Assert.Equal(InstanceState.Upgrading, refused.State);

        // A second upgrade is refused for the same reason — the row is taken, not re-entrant,
        // so an operator re-pinning the tag mid-roll waits rather than joining the run.
        using var third = h.NewStore();
        Assert.Equal(
            InstanceState.Upgrading,
            (await RefusedAsync(h.NewProvisioner(third).UpgradeAsync(i.Id, "v3", default))).State);

        // And it took nothing with it on the way out.
        var held = await ReadAsync(second, i.Id);
        Assert.Equal(InstanceState.Upgrading, held.State);
        Assert.Null(held.DestroyedAt);
        Assert.Contains("docket-acme-pgdata", h.Substrate.Volumes);
        Assert.Contains("docket-acme-pg", h.Substrate.Containers.Keys);
        Assert.Equal(2, h.Caddy.Routes.Count);

        release.SetResult();
        await upgrade;
        Assert.Equal(InstanceState.Ready, (await ReadAsync(h.Db, i.Id)).State);
    }

    /// <summary>
    /// The mirror: an upgrade cannot start while a destroy is tearing the Instance down,
    /// which is what stops a saga recreating containers onto a tombstone.
    /// </summary>
    [Fact]
    public async Task An_upgrade_cannot_start_while_a_destroy_holds_the_row()
    {
        using var h = new SagaHarness();
        var i = await ReadyInstanceAsync(h);

        var park = Gate();
        var release = Gate();
        h.Substrate.BeforeOp = async op =>
        {
            if (!op.StartsWith("rm:")) return;
            park.TrySetResult();
            await release.Task;
        };

        var destroy = h.Provisioner.DestroyAsync(i.Id, default);
        await park.Task;

        using var second = h.NewStore();
        var refused = await RefusedAsync(h.NewProvisioner(second).UpgradeAsync(i.Id, "v2", default));
        Assert.Equal(InstanceState.Destroying, refused.State);
        Assert.Equal("v1", (await ReadAsync(second, i.Id)).ImageTag);

        release.SetResult();
        await destroy;
        Assert.Equal(InstanceState.Destroyed, (await ReadAsync(h.Db, i.Id)).State);
    }

    /// <summary>
    /// The other race the filed defect named: an operator re-pinning the tag of a stalled
    /// Instance at the same moment as a retry of its provision. Both are legal from
    /// <c>failed</c>, so the row has to arbitrate — and the loser must not proceed, since
    /// two sagas over one instance would contend for the same containers and ports.
    /// </summary>
    [Fact]
    public async Task An_upgrade_and_a_retry_of_the_same_failed_instance_admit_one()
    {
        using var h = new SagaHarness();
        await h.AddHostAsync();
        var created = await h.Creator.CreateAsync(new CreateInstanceRequest("acme", null, "v1", null), default);

        var park = Gate();
        var release = Gate();
        var failRelay = true;
        h.Substrate.BeforeOp = async op =>
        {
            if (failRelay && op == "run:docket-acme-relay")
                throw new InvalidOperationException("relay would not come up");
            if (op != "pull:docket-mcp:v2") return;
            park.TrySetResult();
            await release.Task;
        };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Provisioner.ProvisionAsync(created.Id, default));
        failRelay = false;

        // The upgrade wins the row and parks re-pulling images.
        var upgrade = h.Provisioner.UpgradeAsync(created.Id, "v2", default);
        await park.Task;

        using var second = h.NewStore();
        var refused = await RefusedAsync(h.NewProvisioner(second).ProvisionAsync(created.Id, default));
        Assert.Equal(InstanceState.Upgrading, refused.State);

        release.SetResult();
        await upgrade;
        var done = await ReadAsync(h.Db, created.Id);
        Assert.Equal(InstanceState.Ready, done.State);
        Assert.Equal("v2", done.ImageTag);
        // A successful roll leaves no stale failed step behind — claiming the row cleared it.
        Assert.Null(done.FailedStep);
    }
}

/// <summary>
/// The claim under a real race, which is a property of Postgres rather than of C#: two
/// operations that are each legal on their own reach for the same row at the same moment,
/// and the conditional UPDATE has to admit exactly one. The in-memory provider the rest of
/// the suite runs on implements neither <c>ExecuteUpdate</c> nor row locking, so this is
/// the only place the claim's real mechanism is exercised.
/// </summary>
[Collection(MetaPostgresCollection.Name)]
public sealed class LifecycleClaimPostgresTests(MetaPostgresFixture pg)
{
    [SkippableFact]
    public async Task Two_operations_racing_for_one_ready_instance_admit_exactly_one()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var store = pg.NewContext();
        // The point of these two is the relational claim path; a store that quietly was not
        // Postgres would exercise the in-memory fallback and prove nothing.
        Assert.True(store.Database.IsNpgsql());
        await ResetAsync(store);
        using var h = new SagaHarness(store: store, stores: pg.NewContext);
        await h.AddHostAsync();
        var created = await h.Creator.CreateAsync(new CreateInstanceRequest("race", null, "v1", null), default);
        await h.Provisioner.ProvisionAsync(created.Id, default);

        // Both operations are legal from `ready`, and whichever wins parks on its first
        // substrate call still holding the row — so the loser cannot slip in behind a
        // winner that finished, and "exactly one" is a statement about the claim.
        var park = Gate();
        var release = Gate();
        h.Substrate.BeforeOp = async _ =>
        {
            park.TrySetResult();
            await release.Task;
        };

        await using var upgradeStore = pg.NewContext();
        await using var destroyStore = pg.NewContext();
        var upgrade = Task.Run(() => h.NewProvisioner(upgradeStore).UpgradeAsync(created.Id, "v2", default));
        var destroy = Task.Run(() => h.NewProvisioner(destroyStore).DestroyAsync(created.Id, default));

        // The winner never completes while parked, so the first task to finish is the loser.
        // Bounded: if the claim admitted BOTH, neither would ever finish, and a hung run says
        // far less than a failed assertion does.
        var loser = await Task.WhenAny(upgrade, destroy, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(loser == upgrade || loser == destroy,
            "neither operation was refused: both claimed the same row");
        var refused = await Assert.ThrowsAsync<InstanceBusyException>(() => loser);
        Assert.Contains(refused.State, new[]
        {
            InstanceState.Ready, InstanceState.Upgrading, InstanceState.Destroying,
        });

        release.SetResult();
        var winner = loser == upgrade ? destroy : upgrade;
        await winner;

        // Exactly one of the two happened — never a tombstone on the new tag, or both.
        await using var read = pg.NewContext();
        var row = await read.Instances.AsNoTracking().SingleAsync(i => i.Id == created.Id);
        if (winner == destroy)
        {
            Assert.Equal(InstanceState.Destroyed, row.State);
            Assert.Equal("v1", row.ImageTag);
        }
        else
        {
            Assert.Equal(InstanceState.Ready, row.State);
            Assert.Equal("v2", row.ImageTag);
        }
    }

    /// <summary>
    /// The sweep's own query, translated by the real provider. It is LINQ over a
    /// value-converted enum, an array containment, and an aggregate over the child
    /// checkpoints — each of which either translates on Postgres or throws there — and the
    /// sweep swallows its own exceptions on purpose (a store briefly out of reach must cost
    /// a sweep, not the sweeper). A translation failure would therefore disable
    /// reconciliation in production while every in-memory suite stayed green, since the
    /// in-memory provider translates nothing.
    /// </summary>
    [SkippableFact]
    public async Task The_sweep_selects_and_finishes_an_interrupted_upgrade_on_real_postgres()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var store = pg.NewContext();
        // The point of these two is the relational claim path; a store that quietly was not
        // Postgres would exercise the in-memory fallback and prove nothing.
        Assert.True(store.Database.IsNpgsql());
        await ResetAsync(store);
        using var h = new SagaHarness(store: store, stores: pg.NewContext);
        await h.AddHostAsync();
        var created = await h.Creator.CreateAsync(new CreateInstanceRequest("sweep", null, "v1", null), default);
        await h.Provisioner.ProvisionAsync(created.Id, default);

        // Abandon an upgrade mid-flight, exactly as a killed process would.
        var park = Gate();
        var release = Gate();
        h.Substrate.BeforeOp = async op =>
        {
            if (op != "run:docket-sweep-mcp") return;
            park.TrySetResult();
            await release.Task;
        };
        var abandoned = h.Provisioner.UpgradeAsync(created.Id, "v2", default);
        await park.Task;
        h.Substrate.BeforeOp = null;

        Assert.Equal(1, await h.NewReconciler().SweepAsync(default));

        await using var read = pg.NewContext();
        var row = await read.Instances.AsNoTracking().SingleAsync(i => i.Id == created.Id);
        Assert.Equal(InstanceState.Ready, row.State);
        Assert.Equal("v2", row.ImageTag);
        Assert.Equal("docket-mcp:v2", h.Substrate.Containers["docket-sweep-mcp"].Spec.Image);

        release.SetResult();
        await Task.WhenAny(abandoned, Task.Delay(TimeSpan.FromSeconds(10)));
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Empties the store so the sweep — which is deliberately store-wide — sees only this
    /// test's Instance, and not whatever a sibling suite in this collection left behind.
    /// </summary>
    private static Task ResetAsync(MetaDbContext db) =>
        db.Database.ExecuteSqlRawAsync("TRUNCATE instances, instance_steps, hosts RESTART IDENTITY CASCADE");
}
