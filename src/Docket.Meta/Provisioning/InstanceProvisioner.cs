using Docket.Meta.Data;
using Docket.Meta.Edge;
using Docket.Meta.Substrate;
using Microsoft.EntityFrameworkCore;

namespace Docket.Meta.Provisioning;

/// <summary>
/// The provisioning saga (design note §2). Realizes an Instance's recorded desired
/// state through the ordered <see cref="ProvisionStep"/>s, each idempotent (the
/// substrate adopts existing Docker objects by name/label) and each a persisted
/// checkpoint (<see cref="InstanceStepRow"/>). A crash mid-provision leaves the
/// checkpoints behind, so re-running resumes from the first not-Done step and
/// converges — it never regenerates secrets (those are minted once, before this
/// runs) or duplicates objects. Also owns suspend/resume, destroy, and image
/// upgrade — the other lifecycle transitions, all likewise idempotent.
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
    /// Runs (or resumes) the provisioning saga for one instance to <c>ready</c>. On a
    /// step failure the instance is left <c>failed(step)</c> and the exception
    /// propagates; the reconciler or an operator retry re-enters here and resumes.
    /// </summary>
    public async Task ProvisionAsync(Guid instanceId, CancellationToken ct)
    {
        var i = await LoadAsync(instanceId, ct)
            ?? throw new InvalidOperationException($"instance {instanceId} not found");
        if (i.State is InstanceState.Destroyed)
            return;

        var sub = substrates.For(i.Host!);

        // A retry after a failure re-enters provisioning; clear the failed marker so
        // the UI reflects the in-flight attempt.
        i.State = InstanceState.Provisioning;
        i.FailedStep = null;
        await db.SaveChangesAsync(ct);

        foreach (var step in Order)
        {
            await RunStepAsync(i, step, ct, step switch
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
            });
        }

        i.State = InstanceState.Ready;
        await db.SaveChangesAsync(ct);
        log.LogInformation("instance {Name} ({Id}) provisioned → ready", i.Name, i.Id);
    }

    /// <summary>Stop containers + drop routes; keep volume, secrets, and config for resume (design note §2).</summary>
    public async Task SuspendAsync(Guid instanceId, CancellationToken ct)
    {
        var i = await LoadAsync(instanceId, ct) ?? throw NotFound(instanceId);
        if (i.State is InstanceState.Destroyed)
            return;
        var sub = substrates.For(i.Host!);

        await BestEffortRemoveRoutesAsync(i, ct);
        await sub.StopContainerAsync(InstanceNaming.RelayContainer(i.Name), ct);
        await sub.StopContainerAsync(InstanceNaming.McpContainer(i.Name), ct);
        await sub.StopContainerAsync(InstanceNaming.PgContainer(i.Name), ct);

        i.State = InstanceState.Suspended;
        i.SuspendedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        log.LogInformation("instance {Name} suspended", i.Name);
    }

    /// <summary>Start containers back up (pg → mcp → relay), restore routes, verify (design note §2).</summary>
    public async Task ResumeAsync(Guid instanceId, CancellationToken ct)
    {
        var i = await LoadAsync(instanceId, ct) ?? throw NotFound(instanceId);
        if (i.State is InstanceState.Destroyed)
            return;
        var sub = substrates.For(i.Host!);

        await sub.StartContainerAsync(InstanceNaming.PgContainer(i.Name), ct);
        await WaitForHealthyAsync(sub, InstanceNaming.PgContainer(i.Name), PgHealthTimeout, ct);
        await sub.StartContainerAsync(InstanceNaming.McpContainer(i.Name), ct);
        if (!await probe.WaitForPlaneAsync(i.Host!, i.McpPublishedPort!.Value, PlaneReadyTimeout, ct))
            throw new InvalidOperationException("mcp did not become ready on resume");
        await sub.StartContainerAsync(InstanceNaming.RelayContainer(i.Name), ct);
        await UpsertRoutesAsync(i, ct);

        i.State = InstanceState.Ready;
        i.SuspendedAt = null;
        await db.SaveChangesAsync(ct);
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
    /// Roll the instance to a new image tag: recreate mcp + relay on the new tag,
    /// leaving Postgres and its volume untouched (migrations run on mcp startup).
    /// Ports and routes are unchanged — the new containers republish the same host
    /// ports — but routes are re-asserted idempotently (design note §2, spec §3).
    /// </summary>
    public async Task UpgradeAsync(Guid instanceId, string newTag, CancellationToken ct)
    {
        var i = await LoadAsync(instanceId, ct) ?? throw NotFound(instanceId);
        if (i.State is InstanceState.Destroyed)
            throw new InvalidOperationException("cannot upgrade a destroyed instance");
        var sub = substrates.For(i.Host!);

        i.ImageTag = newTag;
        await db.SaveChangesAsync(ct);

        await sub.EnsureImageAsync(recipe.McpImage(i), ct);
        await sub.EnsureImageAsync(recipe.RelayImage(i), ct);

        await sub.RemoveContainerAsync(InstanceNaming.McpContainer(i.Name), ct);
        await sub.RemoveContainerAsync(InstanceNaming.RelayContainer(i.Name), ct);

        i.McpContainerId = await sub.EnsureContainerAsync(recipe.Mcp(i), ct);
        if (!await probe.WaitForPlaneAsync(i.Host!, i.McpPublishedPort!.Value, PlaneReadyTimeout, ct))
            throw new InvalidOperationException("mcp did not become ready after upgrade");
        i.RelayContainerId = await sub.EnsureContainerAsync(recipe.Relay(i), ct);
        await UpsertRoutesAsync(i, ct);

        i.State = InstanceState.Ready;
        await db.SaveChangesAsync(ct);
        log.LogInformation("instance {Name} upgraded → {Tag}", i.Name, newTag);
    }

    // ── internals ──────────────────────────────────────────────────────────

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
