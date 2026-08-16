using Docket.Contracts;
using Docket.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Docket.ControlPlane;

/// <summary>
/// Maps inbound runner events (§10) onto the task store and the connection
/// registry. Liveness signals refresh per-task activity; an <c>exited</c> for a
/// still-working task is a liveness loss and requeues it — unless it is the echo of a
/// kill the plane itself ordered, which it has already accounted for (#84); a
/// <c>rebooted</c> requeues everything that machine held. Authority stays with the state machine
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

            case TurnEndedEvent te:
                await HandleTurnEndedAsync(te, ct);
                break;

            case ExitedEvent e:
                await HandleExitedAsync(e, ct);
                break;

            case RebootedEvent r:
                await HandleRebootedAsync(r, ct);
                break;

            case UsageReportedEvent u:
                // §10 telemetry ingest / §12 measured view: the harness's own account of what
                // this dispatch consumed. Deliberately NOT a liveness or progress signal — a
                // usage report says what has already been spent, and a worker wedged mid-turn
                // can still emit one, so refreshing either clock here would let an accounting
                // line keep a hung task alive. Persisted only.
                await WithStoreAsync(store => store.RecordUsageAsync(u, ct));
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

    /// <summary>
    /// §10 <c>turn-ended</c>: an ACP worker stopped talking. Whether that is news depends
    /// entirely on what the task did first.
    ///
    /// <para>Almost always it is not. A worker reports its result over MCP and waits for the
    /// tool to return before ending its turn, so the working → verifying transition is
    /// already committed by the time this arrives; the same holds for a worker that asked and
    /// is now blocked_on_input. Both leave nothing to do.</para>
    ///
    /// <para>A task still in <c>working</c> is the case that matters, and it is the one the
    /// task model could not produce. There, the turn ending is the worker declining to say
    /// anything at all — and because the ACP session outlives the turn, the process stays up,
    /// keeps heartbeating, and neither liveness clock will ever fire on it. Nothing else in
    /// the plane would notice, so the dispatch would hang until a human did. It requeues
    /// against the §9 check 7 infrastructure cap, exactly as the equivalent silent exit used
    /// to, with its own reason on the trail (<see cref="LivenessLossReason.TurnEndedWithoutResult"/>)
    /// so "the agent stopped" stays distinguishable from "the harness died".</para>
    ///
    /// <para>The stop reason is logged rather than stored: <c>max_tokens</c> and <c>refusal</c>
    /// are the difference between a worker that ran out of room and one that would not do the
    /// task, and the requeue about to happen helps only the first. Giving it a home on the
    /// task row is §12 work, not this.</para>
    /// </summary>
    private async Task HandleTurnEndedAsync(TurnEndedEvent te, CancellationToken ct)
    {
        // A turn the plane itself ended must never be read as the worker going quiet. The
        // wire names only the task, never the attempt (the same limitation HandleExitedAsync
        // works around), so a kill, a requeue and a fast redispatch can put a healthy
        // successor in `working` before the dead session's last turn-ended arrives — and
        // without this guard that successor would be requeued for its predecessor's silence.
        //
        // Peek rather than consume: one killed session yields a turn-ended AND an exited, and
        // the expectation has to survive this to still be here for that. Note what is NOT
        // used — the agent's own `stopReason`. `cancelled` looks like it identifies exactly
        // this case and does not: grok answers `cancelled` on turns the plane never touched
        // (measured 2026-08-16), which would make every wedged grok worker invisible here.
        if (registry.HasCommandedExit(te.Task))
        {
            logger.LogDebug(
                "runner turn-ended for task {Task} is the plane's own kill echoing back", te.Task);
            return;
        }

        await WithStoreAsync(async store =>
        {
            if (await store.GetStateAsync(te.Task, ct) != TaskState.Working)
                return;

            logger.LogInformation(
                "runner turn-ended for task {Task} with stopReason {StopReason} while still working — "
                + "the worker ended its turn without reporting a result or asking a question",
                te.Task, te.StopReason ?? "(none)");
            await store.ApplyAsync(
                te.Task, new LivenessLost(LivenessLossReason.TurnEndedWithoutResult), ct);
        });
    }

    private async Task HandleExitedAsync(ExitedEvent e, CancellationToken ct)
    {
        // §10, #84: the plane kills the harness of a dispatch it has just requeued, and the
        // runner reports that death like any other. This is that echo, so there is nothing
        // to do — the requeue already happened, with the clock that fired as its reason, and
        // it already untracked the attempt. Treating it as news would instead requeue
        // whatever attempt is current by now (the wire names only the task, so the event
        // cannot say which attempt died) and untrack a successor that is running fine —
        // taking a second requeue off the §9 check 7 cap and leaving the successor with no
        // clock over it. Consuming the expectation here is what makes the plane's own kill
        // safe to send at all; see RunnerConnectionRegistry.SendKillAsync.
        if (registry.ConsumeCommandedExit(e.Task))
        {
            logger.LogDebug(
                "runner exited for task {Task} (code {Code}) is the plane's own kill echoing back",
                e.Task, e.ExitCode);
            return;
        }

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

        // §6/§11: blocked_on_input keeps the lease on this machine so the sweeper
        // can still find it (MachineFor). The process itself may have exited — an
        // ACP session that crashed, or a worker that asked and then died — and that
        // is not a failure: mark the process gone so an answer redispatches instead
        // of sending PromptCommand into a dead session. A still-up session never
        // reaches this handler. Every other state is done here or already handled.
        if (state == TaskState.BlockedOnInput)
            registry.MarkProcessGone(e.Task);
        else
            registry.Untrack(e.Task);
    }

    /// <summary>
    /// §10 requeue-on-disconnect: the socket dropped, so the machine is gone and
    /// everything it held requeues — the same fact, and the same reason, as a reboot.
    ///
    /// <para>The caller passes the tasks it held rather than letting this look them up,
    /// because the connection is deliberately unregistered <em>first</em> (#87): the
    /// requeue below commits a <c>pg_notify</c> that wakes the dispatch loop, and a
    /// still-registered dying connection would take one of these tasks straight back onto
    /// its dead socket and burn a second requeue as
    /// <see cref="LivenessLossReason.AckTimeout"/>. Unregistering first closes that window,
    /// and <see cref="RunnerConnectionRegistry.Unregister"/> hands back the held set so
    /// nothing is lost by asking early.</para>
    /// </summary>
    public async Task HandleDisconnectAsync(
        string machineId, IReadOnlyList<TaskId> held, CancellationToken ct = default) =>
        await RequeueHeldAsync(machineId, held, ct);

    private async Task HandleRebootedAsync(RebootedEvent r, CancellationToken ct) =>
        // §10 runner restart: the runner adopted nothing, so every task it held
        // requeues against the infrastructure counter (§6). Unlike the disconnect path
        // the connection is live here — docketd announced itself on a working socket —
        // so the held set is read from the registry as before.
        await RequeueHeldAsync(r.MachineId, registry.TasksOn(r.MachineId), ct);

    private async Task RequeueHeldAsync(
        string machineId, IReadOnlyList<TaskId> held, CancellationToken ct)
    {
        if (held.Count == 0)
            return;
        await WithStoreAsync(async store =>
        {
            foreach (var task in held)
            {
                var state = await store.GetStateAsync(task, ct);
                if (state is TaskState.Working or TaskState.BlockedOnInput)
                    await store.ApplyAsync(task, new LivenessLost(LivenessLossReason.MachineReboot), ct);
            }
        });
        // A no-op for the disconnect path (the connection, and its tracking with it, is
        // already gone); the reboot path needs it.
        foreach (var task in held)
            registry.Untrack(task);
        logger.LogInformation(
            "requeued {Count} task(s) held by machine {Machine}", held.Count, machineId);
    }

    private async Task WithStoreAsync(Func<TaskStore, Task> action)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<TaskStore>();
        await action(store);
    }
}
