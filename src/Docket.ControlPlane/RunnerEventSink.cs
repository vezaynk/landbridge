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
    TranscriptWaiters transcripts,
    ProcessControlRelay processes,
    ILogger<RunnerEventSink> logger)
{
    public async Task HandleAsync(RunnerEvent evt, CancellationToken ct = default)
    {
        switch (evt)
        {
            case StartedEvent s:
                // §10: started confirms the harness is up; the task stays tracked
                // (requeue-on-disconnect still applies), activity refreshes.
                registry.RecordProgress(s.Task);
                break;

            case SessionStartedEvent ss:
                // §11 resume: the harness reported its opaque session ref. Stamp it
                // onto the task row verbatim (never interpreted — like
                // ResultReference/TraceContext) so a later park carries it and
                // redispatch resumes the transcript. A session-init is also forward
                // progress, so refresh activity like the other liveness signals.
                registry.RecordProgress(ss.Task);
                await WithStoreAsync(store => store.StampHarnessSessionRefAsync(ss.Task, ss.SessionRef, ct));
                break;

            case AliveEvent a:
                // §10: docketd's periodic "this harness process still exists" for a
                // supervised task. Refreshes ONLY the aliveness clock — it is not
                // progress, and treating it as progress would make a wedged agent
                // undetectable. It is what keeps an idle-but-alive worker (a long
                // build, a service being babysat) from being requeued every minute.
                registry.RecordAlive(a.Task);
                break;

            case ToolCallEvent t:
                registry.RecordProgress(t.Task);
                break;

            case SubagentSpawnedEvent sub:
                // Progressive-enhancement progress signal (§10): refresh per-task
                // liveness like any inbound signal, and — §12, #50 — persist it as a
                // task event row so it shows on the dashboard as progress (subagent
                // lineage), rather than being visible only as a liveness ping.
                registry.RecordProgress(sub.Task);
                await WithStoreAsync(store =>
                    store.RecordSubagentSpawnAsync(sub.Task, sub.AgentId, sub.ParentAgentId, ct));
                break;

            case ExitedEvent e:
                await HandleExitedAsync(e, ct);
                break;

            case RebootedEvent r:
                await HandleRebootedAsync(r, ct);
                break;

            case AuthFailedEvent af:
                // §11/§12, #50: persist the structured facts as a task event row so
                // the dashboard can surface them (the remediation menu itself is a
                // later step). Still logged — an auth failure is an operator signal —
                // but no longer log-only.
                logger.LogWarning(
                    "runner auth-failed: task={Task} op={Operation} target={Target} code={Code} scope={Scope}",
                    af.Task, af.Operation, af.Target, af.ErrorCode, af.MissingScope);
                await WithStoreAsync(store =>
                    store.RecordAuthFailureAsync(af.Task, af.Operation, af.Target, af.ErrorCode, af.MissingScope, ct));
                break;

            case ForwardOpenedEvent fo:
                // §8.3: the consumer end bound its loopback listener and reported
                // the port. Hand it to the open_forward call parked on this
                // forward id so it can return {host, port} to the worker.
                forwards.Complete(fo.ForwardId, fo.Port);
                break;

            case ProcessStartedEvent or ProcessStoppedEvent or ProcessWrittenEvent:
                // §10: hand the machine's answer to the parked start/stop/write call. Like the
                // transcript reply this is a TrySetResult and nothing more — the sink is on the
                // receive loop, so awaiting anything here would delay heartbeats and alive
                // events behind it.
                processes.Complete(evt);
                break;

            case TranscriptChunkEvent tc:
                // §12 serving: hand the range to the dashboard read parked on this request
                // id. Deliberately NOT a liveness signal — a transcript read is an operator
                // pulling old bytes off a machine, not the task making progress, and the
                // task in question is terminal by the time it can be read at all. Refreshing
                // either clock here would let a read revive a dead task's liveness.
                //
                // This case must stay non-blocking: the sink runs on the runner socket's
                // receive loop, so awaiting anything downstream of the operator's HTTP
                // response would stall every other inbound frame behind it — including the
                // heartbeats the liveness scan requeues tasks over. Completing a waiter is
                // a TrySetResult; the reader's cursor carries the back-pressure instead.
                transcripts.Complete(tc);
                break;

            case ForwardClosedEvent fc:
                // §8.3: a forward's splice ended (either side closed) or it never
                // opened. Unblock any open_forward still waiting on this id — a
                // no-op once the forward opened and the waiter was removed. The
                // grant is single-use and expires on its own, so there is no other
                // per-forward bookkeeping to unwind here.
                // §8.2/§8.3: prefer the machine's own reason when it has one — "the
                // service backing this registration is not running" is actionable in a
                // way "an end closed" is not.
                forwards.Fail(fc.ForwardId, fc.Refusal ?? "the producer or consumer end closed");
                logger.LogInformation("runner forward-closed: task={Task} forward={ForwardId}", fc.Task, fc.ForwardId);
                break;
        }
    }

    private async Task HandleExitedAsync(ExitedEvent e, CancellationToken ct)
    {
        TaskState? state = null;
        await WithStoreAsync(async store =>
        {
            state = await store.GetStateAsync(e.Task, ct);
            // §10: a death after started may have side effects, so a still-working
            // task requeues against the infrastructure counter. If it already
            // reached verifying/terminal (worker reported first), the exit is
            // expected and moot. Exit-without-submit refinement deferred.
            //
            // The reason is the exit itself (#73), not a liveness timeout: nothing timed
            // out here, the process died, and telling the two apart in the trail is the
            // difference between "the harness crashed" and "the daemon went quiet".
            if (state == TaskState.Working)
                await store.ApplyAsync(e.Task, new LivenessLost(LivenessLossReason.ProcessExited), ct);
        });

        // §6/§11: blocked_on_input holds a task whose harness process is *expected*
        // to be gone — a headless worker that asks a question ends its turn and its
        // process exits, and per-task liveness is suspended there. That exit is not
        // a failure and it is not a disconnect: the machine still holds the lease
        // until the wait-TTL sweeper parks it (blocked_on_input → parked) or the
        // machine itself dies (blocked_on_input → submitted). The sweeper resolves a
        // blocked task's machine through RunnerConnectionRegistry.MachineFor, so
        // untracking here would hide the task from it — stranding the task in
        // blocked_on_input forever (never parked on TTL, never requeued on machine
        // death). So leave a blocked task tracked: the sweeper untracks on its own
        // park/requeue transition (WaitTtlSweeper.TryApplyAsync), and a
        // wake→redispatch re-tracks it, so this is consistent, not a leak. Every
        // other state is done here or already handled — working just requeued;
        // verifying/terminal are moot; a parked task's affinity lives in its park
        // record — so untrack as before.
        if (state != TaskState.BlockedOnInput)
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
