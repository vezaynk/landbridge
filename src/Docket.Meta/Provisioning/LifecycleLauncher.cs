using System.Collections.Concurrent;

namespace Docket.Meta.Provisioning;

/// <summary>
/// A transient "operation in flight" marker per instance, for UI feedback (design
/// note §1/§6). Lifecycle actions run in the background (provisioning + resume +
/// upgrade wait on the plane and would outlast an HTTP request), so the persisted
/// state alone can read stale between click and completion — this bridges the gap
/// with a live verb ("resuming…") the auto-refreshing panel shows over the state.
/// </summary>
public sealed class OperationTracker
{
    private readonly ConcurrentDictionary<Guid, string> _busy = new();

    public void Mark(Guid instanceId, string verb) => _busy[instanceId] = verb;
    public void Clear(Guid instanceId) => _busy.TryRemove(instanceId, out _);
    public string? Busy(Guid instanceId) => _busy.GetValueOrDefault(instanceId);
}

/// <summary>
/// Runs a lifecycle transition in the background against its own DI scope so the
/// operator's request returns immediately. Exceptions are logged and leave the
/// instance in its persisted state (failed/suspended/ready) for an idempotent
/// re-click — the transitions themselves are all resumable (design note §2).
/// </summary>
public sealed class LifecycleLauncher(
    IServiceScopeFactory scopes,
    OperationTracker tracker,
    ILogger<LifecycleLauncher> log)
{
    public void Provision(Guid id) => Run(id, "provisioning", (p, ct) => p.ProvisionAsync(id, ct));
    public void Suspend(Guid id) => Run(id, "suspending", (p, ct) => p.SuspendAsync(id, ct));
    public void Resume(Guid id) => Run(id, "resuming", (p, ct) => p.ResumeAsync(id, ct));
    public void Destroy(Guid id) => Run(id, "destroying", (p, ct) => p.DestroyAsync(id, ct));
    public void Upgrade(Guid id, string tag) => Run(id, "upgrading", (p, ct) => p.UpgradeAsync(id, tag, ct));

    private void Run(Guid id, string verb, Func<InstanceProvisioner, CancellationToken, Task> action)
    {
        tracker.Mark(id, verb);
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                var provisioner = scope.ServiceProvider.GetRequiredService<InstanceProvisioner>();
                await action(provisioner, CancellationToken.None);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "instance {Id}: {Verb} did not complete; left in persisted state", id, verb);
            }
            finally
            {
                tracker.Clear(id);
            }
        });
    }
}
