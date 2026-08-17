using Landbridge.Meta.Data;
using Landbridge.Meta.Edge;
using Landbridge.Meta.Substrate;
using Microsoft.EntityFrameworkCore;

namespace Landbridge.Meta.Provisioning;

/// <summary>
/// Refusal of a lifecycle transition because the Instance row was not in a state this
/// transition may start from — nearly always because another operation holds it (the
/// row reads <c>upgrading</c>, <c>destroying</c>, …). Thrown rather than returned
/// because every caller runs the transition in the background and only ever logs the
/// outcome; the operator learns the answer from the state the panel renders.
/// </summary>
public sealed class InstanceBusyException(Guid instanceId, InstanceState state)
    : Exception($"instance {instanceId} is {state.ToString().ToLowerInvariant()}: " +
                "another lifecycle operation holds it, or this one is not legal from here")
{
    public Guid InstanceId { get; } = instanceId;

    /// <summary>The state the row was in when the claim was refused.</summary>
    public InstanceState State { get; } = state;
}

/// <summary>
/// The provisioning saga (design note §2). Realizes an Instance's recorded desired
/// state through the ordered <see cref="ProvisionStep"/>s, each idempotent (the
/// substrate adopts existing Docker objects by name/label, and replaces a container
/// running some other image) and each a persisted checkpoint
/// (<see cref="InstanceStepRow"/>). A crash mid-saga leaves the checkpoints behind, so
/// re-running resumes from the first not-Done step and converges — it never regenerates
/// secrets (those are minted once, before this runs) or duplicates objects.
///
/// <para><b>Resume and upgrade are this saga, not sequences beside it.</b> They differ
/// from a provision in exactly two respects: what they record as the new desired state
/// (an upgrade writes the new <see cref="InstanceRow.ImageTag"/>) and which checkpoints
/// that intent falsifies (<see cref="ResumeInvalidates"/>, <see cref="UpgradeInvalidates"/>).
/// They then run the same ordered steps, which is what makes them checkpointed and
/// resumable too. Written as their own container sequences they were neither: an upgrade
/// removed both plane containers up front and recorded nothing in flight, so a readiness
/// timeout — the ordinary failure of the thing it was waiting for — threw after the
/// removals with the row still persisted <c>ready</c>, no relay container, and only a
/// warning in the log. Nothing healed that, because nothing could see it.</para>
///
/// <para><b>One operation at a time, enforced by the row itself.</b> Every transition
/// begins by <em>claiming</em> the Instance: a conditional UPDATE that moves it into that
/// operation's in-flight state only from the states the transition is legal from (see
/// <see cref="ClaimAsync"/>). A destroy therefore cannot start while an upgrade is mid-flight
/// — <c>upgrading</c> is not a state destroy claims from — and of two racing operators
/// exactly one wins the row. The claim is the authority; the state checks the panel makes
/// before offering a button are only there to avoid offering a doomed one.</para>
/// </summary>
public sealed class InstanceProvisioner(
    MetaDbContext db,
    ISubstrateFactory substrates,
    ICaddyAdmin caddy,
    InstanceRecipe recipe,
    InstanceHealthProbe probe,
    MetaOptions options,
    TimeProvider clock,
    ILogger<InstanceProvisioner> log)
{
    private static readonly TimeSpan PgHealthTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan PlaneReadyTimeout = TimeSpan.FromSeconds(180);

    private static readonly ProvisionStep[] Order =
    [
        ProvisionStep.PullImages, ProvisionStep.CreateNetwork, ProvisionStep.CreateVolume,
        ProvisionStep.StartPostgres, ProvisionStep.StartMcp, ProvisionStep.StartRelay,
        ProvisionStep.AddRoutes, ProvisionStep.VerifyReady,
    ];

    /// <summary>
    /// What a suspend made untrue: the containers are stopped and the edge routes are gone,
    /// so "started" and "routed" no longer describe the world and the saga has to redo them.
    /// The image pull, network, and volume checkpoints stand — a suspend touched none of them.
    /// </summary>
    private static readonly ProvisionStep[] ResumeInvalidates =
    [
        ProvisionStep.StartPostgres, ProvisionStep.StartMcp, ProvisionStep.StartRelay,
        ProvisionStep.AddRoutes, ProvisionStep.VerifyReady,
    ];

    /// <summary>
    /// What a new image tag made untrue: the new images are not pulled, the two plane
    /// containers run the old ones, and nothing has verified the new plane. Postgres, its
    /// volume, the network, and the routes keep their checkpoints — which is precisely why
    /// an upgrade leaves the data alone (the plane re-migrates on boot) and why the
    /// published ports and routes are unchanged.
    /// </summary>
    private static readonly ProvisionStep[] UpgradeInvalidates =
    [
        ProvisionStep.PullImages, ProvisionStep.StartMcp,
        ProvisionStep.StartRelay, ProvisionStep.VerifyReady,
    ];

    // ── Which states each transition may be claimed from ───────────────────────
    //
    // What these sets leave out is the point. No set below contains another operation's
    // in-flight state, so no two operations can interleave; ContinueFrom is the single
    // deliberate exception, and taking over an abandoned run is the only thing it is for.
    //
    // A set contains its own in-flight state only where something legitimately re-enters
    // that same method on a row already in it: the launch that follows a create (the row is
    // already `provisioning`), and the reconciler finishing an abandoned suspend or destroy,
    // which have no separate takeover path because they have no checkpoints to resume from
    // — re-running either is just re-issuing idempotent stops or removals. Resume and
    // upgrade are absent from their own sets on purpose: a stalled attempt lands in `failed`
    // (which they do claim) and an abandoned one is the reconciler's, so admitting them
    // would only let a second operator's click join a run already under way.

    /// <summary>
    /// What a create or an operator retry may provision from. <c>ready</c> is included
    /// because re-running a completed saga is a legal convergence pass: every step is
    /// checkpointed Done, so it asserts the recorded state and changes nothing. No other
    /// operation's in-flight state is — a retry clicked during an upgrade is refused here,
    /// not merely refused in whichever process the upgrade is running in.
    /// </summary>
    private static readonly InstanceState[] ProvisionFrom =
    [
        InstanceState.Provisioning, InstanceState.Failed, InstanceState.Ready,
    ];

    /// <summary>
    /// What a takeover may claim, and the one source set that includes another operation's
    /// in-flight state — which is precisely what taking over means. An interrupted resume or
    /// upgrade has already recorded its intent (invalidated checkpoints, the new tag), so
    /// all three "wants to be ready" cases are finished by running the same saga.
    /// <c>suspending</c> and <c>destroying</c> are absent: their intent is not "be ready",
    /// so converging them upward would undo what the operator asked for.
    /// </summary>
    private static readonly InstanceState[] ContinueFrom =
    [
        InstanceState.Provisioning, InstanceState.Failed,
        InstanceState.Resuming, InstanceState.Upgrading,
    ];

    private static readonly InstanceState[] ResumeFrom = [InstanceState.Suspended];

    /// <summary>
    /// Upgrade is offered on a <c>failed</c> Instance on purpose: the commonest reason a
    /// provision stalls is a tag that cannot be pulled, and re-pinning it is the fix. It is
    /// not offered on a suspended one — resume first, so there is a running plane to roll.
    /// </summary>
    private static readonly InstanceState[] UpgradeFrom = [InstanceState.Ready, InstanceState.Failed];

    private static readonly InstanceState[] SuspendFrom = [InstanceState.Ready, InstanceState.Suspending];

    /// <summary>
    /// Destroy deliberately cannot start from an in-flight state other than its own — a
    /// teardown racing a saga that is still creating containers would leave Docker objects
    /// behind on a tombstoned row. Nothing becomes permanently undestroyable by that: an
    /// operation that stalls lands in <c>failed</c>, and one that is abandoned is taken over
    /// and lands in <c>failed</c> or <c>ready</c>, both of which destroy claims.
    /// </summary>
    private static readonly InstanceState[] DestroyFrom =
    [
        InstanceState.Ready, InstanceState.Suspended, InstanceState.Failed, InstanceState.Destroying,
    ];

    /// <summary>
    /// Runs (or resumes) the saga for one instance to <c>ready</c>. On a step failure the
    /// instance is left <c>failed(step)</c> and the exception propagates; an operator retry
    /// re-enters here and resumes from that step.
    /// </summary>
    public Task ProvisionAsync(Guid instanceId, CancellationToken ct) =>
        RunToReadyAsync(instanceId, ProvisionFrom, ct);

    /// <summary>
    /// Finishes a saga run that nothing is driving any more — a provision, resume, or upgrade
    /// whose process died, or whose last attempt stalled — and drives it to <c>ready</c>.
    ///
    /// <para>The only caller is <see cref="InstanceReconciler"/>, and this is separate from
    /// <see cref="ProvisionAsync"/> for one reason: it is the sole entry point allowed to
    /// claim a row another operation left in flight (<see cref="ContinueFrom"/>). The
    /// reconciler has already established that no operation in this process holds the row,
    /// which is the fact that makes taking it over safe rather than a second saga on a live
    /// one. Nothing an operator clicks gets that privilege.</para>
    /// </summary>
    internal Task ContinueInterruptedAsync(Guid instanceId, CancellationToken ct) =>
        RunToReadyAsync(instanceId, ContinueFrom, ct);

    private async Task RunToReadyAsync(Guid instanceId, InstanceState[] from, CancellationToken ct)
    {
        var i = await LoadAsync(instanceId, ct)
            ?? throw new InvalidOperationException($"instance {instanceId} not found");
        if (i.State is InstanceState.Destroyed)
            return;

        await ClaimOrThrowAsync(i, InstanceState.Provisioning, from, ct);
        await RunSagaAsync(i, ct);
        log.LogInformation("instance {Name} ({Id}) provisioned → ready", i.Name, i.Id);
    }

    /// <summary>Stop containers + drop routes; keep volume, secrets, and config for resume (design note §2).</summary>
    public async Task SuspendAsync(Guid instanceId, CancellationToken ct)
    {
        var i = await LoadAsync(instanceId, ct) ?? throw NotFound(instanceId);
        // Already there (or gone): a no-op rather than a refusal, so a double-click is harmless.
        if (i.State is InstanceState.Destroyed or InstanceState.Suspended)
            return;
        var sub = substrates.For(i.Host!);

        await ClaimOrThrowAsync(i, InstanceState.Suspending, SuspendFrom, ct);

        await BestEffortRemoveRoutesAsync(i, ct);
        await sub.StopContainerAsync(InstanceNaming.RelayContainer(i.Name), ct);
        await sub.StopContainerAsync(InstanceNaming.McpContainer(i.Name), ct);
        await sub.StopContainerAsync(InstanceNaming.PgContainer(i.Name), ct);

        i.State = InstanceState.Suspended;
        i.SuspendedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        log.LogInformation("instance {Name} suspended", i.Name);
    }

    /// <summary>
    /// Start the Instance back up: record that being stopped and unrouted is no longer the
    /// desired state (<see cref="ResumeInvalidates"/>), then run the saga, which starts
    /// pg → mcp → relay, restores the routes, and verifies (design note §2).
    /// </summary>
    public async Task ResumeAsync(Guid instanceId, CancellationToken ct)
    {
        var i = await LoadAsync(instanceId, ct) ?? throw NotFound(instanceId);
        if (i.State is InstanceState.Destroyed)
            return;

        await ClaimOrThrowAsync(i, InstanceState.Resuming, ResumeFrom, ct);
        await InvalidateAsync(i, ResumeInvalidates, ct);
        await RunSagaAsync(i, ct);
        log.LogInformation("instance {Name} resumed → ready", i.Name);
    }

    /// <summary>
    /// Tear down every resource for the instance and tombstone the record (destroyed ≠
    /// suspended). Idempotent — each removal no-ops when its object is already gone —
    /// and route removal is best-effort so a down Caddy never blocks teardown.
    /// </summary>
    public async Task DestroyAsync(Guid instanceId, CancellationToken ct)
    {
        var i = await LoadAsync(instanceId, ct) ?? throw NotFound(instanceId);
        if (i.State is InstanceState.Destroyed)
            return;
        var sub = substrates.For(i.Host!);

        await ClaimOrThrowAsync(i, InstanceState.Destroying, DestroyFrom, ct);

        await BestEffortRemoveRoutesAsync(i, ct);
        await sub.RemoveContainerAsync(InstanceNaming.RelayContainer(i.Name), ct);
        await sub.RemoveContainerAsync(InstanceNaming.McpContainer(i.Name), ct);
        await sub.RemoveContainerAsync(InstanceNaming.PgContainer(i.Name), ct);
        await sub.RemoveNetworkAsync(InstanceNaming.NetworkName(i.Name), ct);
        await sub.RemoveVolumeAsync(InstanceNaming.VolumeName(i.Name), ct);

        i.State = InstanceState.Destroyed;
        i.DestroyedAt = clock.GetUtcNow();
        // Hygiene: a tombstone need not keep live secret material around.
        i.DbPassword = "";
        i.RelayBearer = "";
        i.PassphraseHash = "";
        await db.SaveChangesAsync(ct);
        log.LogInformation("instance {Name} destroyed", i.Name);
    }

    /// <summary>
    /// Roll the instance to a new image tag. Records the tag as the new desired state and
    /// invalidates the checkpoints it falsified (<see cref="UpgradeInvalidates"/>); the saga
    /// then replaces mcp + relay on the new tag, leaving Postgres and its volume untouched
    /// (migrations run on mcp startup). Ports and routes are unchanged — the new containers
    /// republish the same host ports (design note §2, spec §3).
    ///
    /// <para>The tag is persisted <em>before</em> any Docker call, so an interrupted upgrade
    /// is recoverable by the ordinary saga: whoever re-enters reads the tag the operator
    /// asked for and the checkpoints that say the plane containers do not match it yet.</para>
    /// </summary>
    public async Task UpgradeAsync(Guid instanceId, string newTag, CancellationToken ct)
    {
        var i = await LoadAsync(instanceId, ct) ?? throw NotFound(instanceId);
        if (i.State is InstanceState.Destroyed)
            throw new InvalidOperationException("cannot upgrade a destroyed instance");

        await ClaimOrThrowAsync(i, InstanceState.Upgrading, UpgradeFrom, ct);

        i.ImageTag = newTag;
        await db.SaveChangesAsync(ct);
        await InvalidateAsync(i, UpgradeInvalidates, ct);

        await RunSagaAsync(i, ct);
        log.LogInformation("instance {Name} upgraded → {Tag}", i.Name, newTag);
    }

    // ── internals ──────────────────────────────────────────────────────────

    /// <summary>
    /// Runs every step in order — skipping those already checkpointed Done — and, once they
    /// all are, records the Instance <c>ready</c>. The terminal write also clears
    /// <see cref="InstanceRow.SuspendedAt"/>, since reaching the end of this saga is exactly
    /// what "no longer suspended" means.
    /// </summary>
    private async Task RunSagaAsync(InstanceRow i, CancellationToken ct)
    {
        var sub = substrates.For(i.Host!);
        foreach (var step in Order)
            await RunStepAsync(i, step, ct, StepBody(i, sub, step));

        i.State = InstanceState.Ready;
        i.SuspendedAt = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// What one step does. Each is idempotent and, where it produces one, returns an
    /// external reference for the step log. Every container step goes through
    /// <see cref="ISubstrate.EnsureContainerAsync"/>, which adopts a container already
    /// running the spec's image, starts a stopped one, and replaces one running a
    /// different image — so the same three steps serve a first provision, a resume, and
    /// an image upgrade.
    /// </summary>
    private Func<CancellationToken, Task<string?>> StepBody(InstanceRow i, ISubstrate sub, ProvisionStep step) =>
        step switch
        {
            ProvisionStep.PullImages => async c =>
            {
                await sub.EnsureImageAsync(recipe.PostgresImage, c);
                await sub.EnsureImageAsync(recipe.McpImage(i), c);
                await sub.EnsureImageAsync(recipe.RelayImage(i), c);
                return $"{recipe.McpImage(i)},{recipe.RelayImage(i)}";
            }
            ,
            ProvisionStep.CreateNetwork => async c =>
            {
                var name = InstanceNaming.NetworkName(i.Name);
                await sub.EnsureNetworkAsync(name, InstanceRecipe.Labels(i, "network"), c);
                i.NetworkName = name;
                return name;
            }
            ,
            ProvisionStep.CreateVolume => async c =>
            {
                var name = InstanceNaming.VolumeName(i.Name);
                await sub.EnsureVolumeAsync(name, InstanceRecipe.Labels(i, "pgdata"), c);
                i.VolumeName = name;
                return name;
            }
            ,
            ProvisionStep.StartPostgres => async c =>
            {
                i.PgContainerId = await sub.EnsureContainerAsync(recipe.Postgres(i), c);
                await WaitForHealthyAsync(sub, InstanceNaming.PgContainer(i.Name), PgHealthTimeout, c);
                return i.PgContainerId;
            }
            ,
            ProvisionStep.StartMcp => async c =>
            {
                i.McpContainerId = await sub.EnsureContainerAsync(recipe.Mcp(i), c);
                if (!await probe.WaitForPlaneAsync(i.Host!, i.McpPublishedPort!.Value, PlaneReadyTimeout, c))
                    throw new InvalidOperationException("mcp did not become ready within timeout");
                return i.McpContainerId;
            }
            ,
            ProvisionStep.StartRelay => async c =>
            {
                i.RelayContainerId = await sub.EnsureContainerAsync(recipe.Relay(i), c);
                return i.RelayContainerId;
            }
            ,
            ProvisionStep.AddRoutes => async c =>
            {
                await UpsertRoutesAsync(i, c);
                i.PublicUrl = InstanceNaming.McpPublicUrl(i.Name, options.Domain);
                return i.PublicUrl;
            }
            ,
            ProvisionStep.VerifyReady => async c =>
            {
                var health = await probe.CheckAsync(sub, i.Host!, i, c);
                if (!health.Ok)
                    throw new InvalidOperationException(
                        $"instance not healthy (pg={health.PgRunning} mcp={health.McpResponding} relay={health.RelayRunning})");
                return "ok";
            }
            ,
            _ => _ => Task.FromResult<string?>(null),
        };

    /// <summary>
    /// Takes the row for this operation, or reports that it could not. One conditional
    /// UPDATE — move the Instance into <paramref name="to"/> only while its state is one of
    /// <paramref name="from"/> — so of two callers racing for the same row exactly one sees a
    /// row affected. Claiming also clears <see cref="InstanceRow.FailedStep"/>: it describes
    /// a stall that this attempt is now retrying, and leaving it behind is how an upgrade
    /// used to persist <c>ready</c> alongside a stale failed step.
    ///
    /// <para><b>On the non-relational path.</b> The EF InMemory provider the saga tests run
    /// on implements neither <c>ExecuteUpdate</c> nor row locking, so there this degrades to
    /// a read-modify-write of the same predicate: the guard's <em>logic</em> (a destroy
    /// refused while an upgrade holds the row) is what those tests exercise, and its
    /// atomicity under concurrency — a property of Postgres, not of C# — is tested against
    /// real Postgres. <c>InstanceCreator</c> makes the same split for the same reason.</para>
    /// </summary>
    private async Task<bool> ClaimAsync(InstanceRow i, InstanceState to, InstanceState[] from, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            var claimed = await db.Instances
                .Where(x => x.Id == i.Id && from.Contains(x.State))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.State, to)
                    .SetProperty(x => x.FailedStep, (ProvisionStep?)null), ct);
            if (claimed == 0)
                return false;
        }
        else if (Array.IndexOf(from, i.State) < 0)
        {
            return false;
        }

        // The tracked row is the one every step below writes through, so bring it in line
        // with what the store now says (on the relational path the UPDATE bypassed it).
        i.State = to;
        i.FailedStep = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task ClaimOrThrowAsync(InstanceRow i, InstanceState to, InstanceState[] from, CancellationToken ct)
    {
        if (!await ClaimAsync(i, to, from, ct))
            throw new InstanceBusyException(i.Id, i.State);
    }

    /// <summary>
    /// Marks checkpoints not-done so the next saga run re-executes them. This is the whole
    /// of what makes resume and upgrade different from a provision: they say which recorded
    /// facts their intent falsified, and the saga does the rest. Steps not named here keep
    /// their checkpoints and are skipped — that is what leaves Postgres and its volume
    /// untouched by an upgrade.
    /// </summary>
    private async Task InvalidateAsync(InstanceRow i, ProvisionStep[] steps, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        foreach (var step in steps)
        {
            if (i.Steps.FirstOrDefault(s => s.Step == step) is not { } row)
                continue;
            row.Status = StepStatus.Pending;
            row.ExternalRef = null;
            row.Error = null;
            row.StartedAt = now;
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task RunStepAsync(InstanceRow i, ProvisionStep step, CancellationToken ct, Func<CancellationToken, Task<string?>> action)
    {
        var row = i.Steps.FirstOrDefault(s => s.Step == step);
        if (row is null)
        {
            row = new InstanceStepRow { Id = Guid.NewGuid(), InstanceId = i.Id, Step = step, StartedAt = clock.GetUtcNow() };
            i.Steps.Add(row);
            db.InstanceSteps.Add(row);
        }
        if (row.Status == StepStatus.Done)
            return;

        try
        {
            var reference = await action(ct);
            row.Status = StepStatus.Done;
            row.ExternalRef = reference;
            row.Error = null;
            row.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            row.Status = StepStatus.Failed;
            row.Error = ex.Message;
            row.UpdatedAt = clock.GetUtcNow();
            // Failed is a state the reconciler sweeps and an operator can retry from — and,
            // being quiescent, one a destroy may claim. Whichever operation was running, its
            // intent is already recorded on the row and in the checkpoints, so re-entering
            // the saga finishes it rather than starting something else.
            i.State = InstanceState.Failed;
            i.FailedStep = step;
            await db.SaveChangesAsync(CancellationToken.None);
            log.LogWarning(ex, "instance {Name} failed at step {Step}", i.Name, step);
            throw;
        }
    }

    private async Task UpsertRoutesAsync(InstanceRow i, CancellationToken ct)
    {
        await caddy.UpsertRouteAsync(
            InstanceNaming.McpRouteId(i.Name),
            InstanceNaming.McpHost(i.Name, options.Domain),
            $"{i.Host!.PublishedHost}:{i.McpPublishedPort}", ct);
        await caddy.UpsertRouteAsync(
            InstanceNaming.RelayRouteId(i.Name),
            InstanceNaming.RelayHost(i.Name, options.Domain),
            $"{i.Host!.PublishedHost}:{i.RelayPublishedPort}", ct);
    }

    private async Task BestEffortRemoveRoutesAsync(InstanceRow i, CancellationToken ct)
    {
        try
        {
            await caddy.RemoveRouteAsync(InstanceNaming.McpRouteId(i.Name), ct);
            await caddy.RemoveRouteAsync(InstanceNaming.RelayRouteId(i.Name), ct);
        }
        catch (Exception ex)
        {
            // A down edge must not block suspend/destroy — a dangling route resolves
            // to a dead upstream (502) until the next reconcile clears it.
            log.LogWarning(ex, "instance {Name}: route removal failed (edge unreachable?); continuing", i.Name);
        }
    }

    private async Task WaitForHealthyAsync(ISubstrate sub, string container, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = clock.GetUtcNow() + timeout;
        while (clock.GetUtcNow() < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var s = await sub.InspectContainerAsync(container, ct);
            if (!s.Exists)
                throw new InvalidOperationException($"container {container} disappeared while waiting for health");
            // No HEALTHCHECK reports as None — running is the best signal we have.
            if (s.Health is HealthState.Healthy || (s.Health is HealthState.None && s.Running))
                return;
            if (s.Health is HealthState.Unhealthy && clock.GetUtcNow() + TimeSpan.FromSeconds(5) > deadline)
                throw new InvalidOperationException($"container {container} is unhealthy");
            await Task.Delay(TimeSpan.FromSeconds(1), clock, ct);
        }
        throw new InvalidOperationException($"container {container} did not become healthy within {timeout}");
    }

    private Task<InstanceRow?> LoadAsync(Guid id, CancellationToken ct) =>
        db.Instances.Include(x => x.Host).Include(x => x.Steps).FirstOrDefaultAsync(x => x.Id == id, ct);

    private static InvalidOperationException NotFound(Guid id) => new($"instance {id} not found");
}
