using System.Collections.Concurrent;

namespace Docket.Meta.Provisioning;

/// <summary>
/// Which instances have a lifecycle operation running <b>in this process</b>, and under
/// what verb (design note §1/§6). Two jobs, both narrow:
///
/// <list type="bullet">
/// <item>the panel renders the verb over the persisted state, so a click reads as
/// "resuming…" rather than as the state it is about to leave;</item>
/// <item><see cref="LifecycleLauncher"/> and <see cref="InstanceReconciler"/> both take a
/// mark before starting anything, so one operation per instance runs here at a time.</item>
/// </list>
///
/// <para><b>This is not the cross-operation guard</b> — that is the Instance row's own
/// state, claimed with a conditional UPDATE (<see cref="InstanceProvisioner"/>), which is
/// what survives a request ending, a process dying, and a second meta. What the mark adds
/// is the one thing a row cannot express: whether a live operation in <em>this</em> process
/// is already driving it. That is exactly the question the reconciler has to answer before
/// taking a row over, and the answer is right for the reason it is process-local — a
/// restart empties this, and a restart is precisely when an in-flight row needs finishing.
/// (A multi-process meta would need a lease column on the row instead; meta is one panel
/// process.)</para>
/// </summary>
public sealed class OperationTracker
{
    private readonly ConcurrentDictionary<Guid, string> _busy = new();

    /// <summary>
    /// Marks an operation as starting, or reports false because one already is. The
    /// check and the write are one atomic operation, so two racing callers cannot both
    /// believe they hold the instance.
    /// </summary>
    public bool TryMark(Guid instanceId, string verb) => _busy.TryAdd(instanceId, verb);

    public void Clear(Guid instanceId) => _busy.TryRemove(instanceId, out _);

    /// <summary>The verb of the operation running here for this instance, or null.</summary>
    public string? Busy(Guid instanceId) => _busy.GetValueOrDefault(instanceId);
}

/// <summary>
/// Runs a lifecycle transition in the background against its own DI scope so the
/// operator's request returns immediately. Exceptions are logged and leave the
/// instance in its persisted state (failed/suspended/ready) for an idempotent
/// re-click — the transitions themselves are all resumable (design note §2).
///
/// <para>Each launch is refused rather than queued when one is already running here for
/// that instance (<see cref="OperationTracker.TryMark"/>), and the transition it starts
/// re-asserts the same exclusion on the row itself. The operator needs no feedback from
/// the refusal: the panel renders the operation that <em>is</em> running.</para>
/// </summary>
public sealed class LifecycleLauncher(
    IServiceScopeFactory scopes,
    OperationTracker tracker,
    ILogger<LifecycleLauncher> log)
{
    public bool TryProvision(Guid id) => TryRun(id, "provisioning", (p, ct) => p.ProvisionAsync(id, ct));
    public bool TrySuspend(Guid id) => TryRun(id, "suspending", (p, ct) => p.SuspendAsync(id, ct));
    public bool TryResume(Guid id) => TryRun(id, "resuming", (p, ct) => p.ResumeAsync(id, ct));
    public bool TryDestroy(Guid id) => TryRun(id, "destroying", (p, ct) => p.DestroyAsync(id, ct));
    public bool TryUpgrade(Guid id, string tag) => TryRun(id, "upgrading", (p, ct) => p.UpgradeAsync(id, tag, ct));

    private bool TryRun(Guid id, string verb, Func<InstanceProvisioner, CancellationToken, Task> action)
    {
        if (!tracker.TryMark(id, verb))
        {
            log.LogInformation("instance {Id}: {Verb} not started — {Busy} is already running", id, verb, tracker.Busy(id));
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                var provisioner = scope.ServiceProvider.GetRequiredService<InstanceProvisioner>();
                await action(provisioner, CancellationToken.None);
            }
            catch (InstanceBusyException ex)
            {
                // The row was held by something this process does not know about (another
                // meta, or a state this transition is not legal from). Not a fault.
                log.LogInformation("instance {Id}: {Verb} refused — {Reason}", id, verb, ex.Message);
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
        return true;
    }
}
