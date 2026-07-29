using Docket.Contracts;
using Docket.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Docket.ControlPlane;

/// <summary>
/// Maps inbound runner events (§10) onto the task store and the connection
/// registry. Liveness signals refresh per-task activity; an <c>exited</c> for a
/// still-working task is a liveness loss and requeues it; a <c>rebooted</c>
/// requeues everything that machine held. Authority stays with the state machine
/// — the sink only expresses timer/liveness facts as <see cref="LivenessLost"/>
/// commands, exactly as §6 says the control plane does.
///
/// The store is scoped (one DbContext per operation), so the sink resolves a
/// fresh <see cref="TaskStore"/> from a scope per event rather than holding one.
/// </summary>
public sealed class RunnerEventSink(
    IServiceScopeFactory scopes,
    RunnerConnectionRegistry registry,
    ForwardWaiters forwards,
    ILogger<RunnerEventSink> logger)
{
    public async Task HandleAsync(RunnerEvent evt, CancellationToken ct = default)
    {
        switch (evt)
        {
            case StartedEvent s:
                // §10: started confirms the harness is up; the task stays tracked
                // (requeue-on-disconnect still applies), activity refreshes.
                registry.RecordActivity(s.Task);
                break;

            case AliveEvent a:
                registry.RecordActivity(a.Task);
                break;

            case ToolCallEvent t:
                registry.RecordActivity(t.Task);
                break;

            case SubagentSpawnedEvent sub:
                // Progressive-enhancement progress signal (§10); treat as activity.
                registry.RecordActivity(sub.Task);
                break;

            case ExitedEvent e:
                await HandleExitedAsync(e, ct);
                break;

            case RebootedEvent r:
                await HandleRebootedAsync(r, ct);
                break;

            case AuthFailedEvent af:
                // §11: recorded for now; remediation rendering is deferred.
                logger.LogWarning(
                    "runner auth-failed: task={Task} op={Operation} target={Target} code={Code} scope={Scope}",
                    af.Task, af.Operation, af.Target, af.ErrorCode, af.MissingScope);
                break;

            case ForwardOpenedEvent fo:
                // §8.3: the consumer end bound its loopback listener and reported
                // the port. Hand it to the open_forward call parked on this
                // forward id so it can return {host, port} to the worker.
                forwards.Complete(fo.ForwardId, fo.Port);
                break;

            case ForwardClosedEvent fc:
                // §8.3: a forward's splice ended (either side closed) or it never
                // opened. Unblock any open_forward still waiting on this id — a
                // no-op once the forward opened and the waiter was removed. The
                // grant is single-use and expires on its own, so there is no other
                // per-forward bookkeeping to unwind here.
                forwards.Fail(fc.ForwardId, "the producer or consumer end closed");
                logger.LogInformation("runner forward-closed: task={Task} forward={ForwardId}", fc.Task, fc.ForwardId);
                break;
        }
    }

    private async Task HandleExitedAsync(ExitedEvent e, CancellationToken ct)
    {
        await WithStoreAsync(async store =>
        {
            var state = await store.GetStateAsync(e.Task, ct);
            // §10: a death after started may have side effects, so a still-working
            // task requeues against the infrastructure counter. If it already
            // reached verifying/terminal (worker reported first), the exit is
            // expected and moot. Exit-without-submit refinement deferred.
            if (state == TaskState.Working)
                await store.ApplyAsync(e.Task, new LivenessLost(LivenessLossReason.LivenessTimeout), ct);
        });
        registry.Untrack(e.Task);
    }

    private async Task HandleRebootedAsync(RebootedEvent r, CancellationToken ct)
    {
        // §10 runner restart: the runner adopted nothing, so every task it held
        // requeues against the infrastructure counter (§6).
        var held = registry.TasksOn(r.MachineId);
        await WithStoreAsync(async store =>
        {
            foreach (var task in held)
            {
                var state = await store.GetStateAsync(task, ct);
                if (state is TaskState.Working or TaskState.BlockedOnInput)
                    await store.ApplyAsync(task, new LivenessLost(LivenessLossReason.MachineReboot), ct);
            }
        });
        foreach (var task in held)
            registry.Untrack(task);
    }

    private async Task WithStoreAsync(Func<TaskStore, Task> action)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<TaskStore>();
        await action(store);
    }
}
