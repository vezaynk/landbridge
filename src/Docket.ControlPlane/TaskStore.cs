using System.Diagnostics;
using Docket.Core;
using Microsoft.EntityFrameworkCore;

namespace Docket.ControlPlane;

/// <summary>
/// The only write path to task state (spec §15: the control plane is the only
/// path to the state machine). Every mutation runs a transition through
/// <see cref="TaskStateMachine"/>, then persists the resulting record, its
/// effects, and an event row in one transaction — so a transition and its
/// consequences (token mint/revoke, service clearing, park record) are
/// atomic, and a NOTIFY fires only if the write commits.
/// </summary>
public sealed class TaskStore(DocketDbContext db, TimeProvider clock)
{
    public async Task<StoreResult> CreateAsync(CreateTask command, CancellationToken ct = default)
    {
        var id = TaskId.New();
        var ns = $"team-{command.Team}/task-{id}";
        var result = TaskStateMachine.Create(command, id, ns);
        if (result is TransitionResult.Rejected r)
            return new StoreResult.Rejected(r.Rule, r.Reason);

        var task = ((TransitionResult.Transitioned)result).Task;
        db.Tasks.Add(new TaskRow
        {
            Id = id.Value,
            TeamId = task.Team.Value,
            Namespace = task.Namespace,
            CompletionMode = task.CompletionMode,
            State = task.State,
            Profile = task.Profile,
            VerificationRetryLimit = task.VerificationRetryLimit,
            // Opaque content the engine never interpreted (§7): persisted verbatim
            // straight off the command, alongside the criteria.
            CompletionCriteria = command.CompletionCriteria,
            Description = command.Description,
            Workspace = command.Workspace,
            // Opaque transport metadata: the ambient W3C traceparent at creation,
            // captured so dispatch can continue the Lead's trace over the wire.
            // Read straight off the ambient Activity here, never through a command
            // field — the engine stays content-free (§7). Null when nothing samples.
            TraceContext = Activity.Current?.Id,
        });
        AppendEvent(id.Value, task.Team.Value, "created", from: null, to: task.State, detail: null);
        await CommitAsync(id.Value, ct);
        return new StoreResult.Applied(task, []);
    }

    /// <summary>Apply a command to an existing task, addressed by id.</summary>
    public async Task<StoreResult> ApplyAsync(TaskId id, TaskCommand command, CancellationToken ct = default)
    {
        var row = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id.Value, ct);
        if (row is null)
            return new StoreResult.NotFound($"no task {id}");

        return await RunTransition(row, command, ct);
    }

    /// <summary>
    /// A Lead's answer to a task awaiting input, routed to whichever §6
    /// transition the task's current state needs — so a Lead answering never has
    /// to know whether the wait-TTL sweeper (§11) parked the task first. One
    /// call, correct outcome either way, and both outcomes requeue for redispatch
    /// rather than resuming in place (§11: "waiting is always the park shape"):
    ///
    ///   blocked_on_input → submitted (<see cref="AnswerInput"/>): the task is
    ///     still waiting; the answer requeues it with a park record built from the
    ///     held-lease machine (<paramref name="leaseMachine"/>) and the row's
    ///     stamped harness session ref, so redispatch resumes the transcript. When
    ///     the machine is gone (<paramref name="leaseMachine"/> is null) the task
    ///     still requeues and redispatch cold-starts elsewhere (§6, §11). This does
    ///     not touch the infrastructure counter (§6, two counters).
    ///   parked → submitted           (<see cref="WakeParked"/>): the sweeper
    ///     already parked it; the answer landing wakes it so redispatch requeues
    ///     it with the park record's machine/directory affinity (§6, §11 — "the
    ///     awaited answer … landed"). parked → submitted is the control plane's
    ///     transition (§6), so the store applies it as the plane once it has
    ///     checked the Lead's Team scope — the same store-level guard
    ///     <see cref="RegisterServiceAsync"/> applies, and exactly the Team check
    ///     the engine enforces on the <see cref="AnswerInput"/> path.
    ///
    /// Any other state falls through to <see cref="AnswerInput"/> and surfaces the
    /// engine's wrong-state rejection unchanged — the behaviour before this method
    /// existed. The answer-vs-sweep race is resolved by the store's optimistic
    /// concurrency exactly as everywhere else: whichever transition commits first
    /// wins, and the loser sees a rejected wrong-state command (the sweeper's
    /// <see cref="WaitTtlExpired"/> on a now-submitted task) or a
    /// <see cref="StoreResult.Conflict"/> — never a lost answer or a double
    /// transition.
    /// </summary>
    public async Task<StoreResult> AnswerOrWakeAsync(
        LeadClaim lead, TaskId id, string? leaseMachine, CancellationToken ct = default)
    {
        var row = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id.Value, ct);
        if (row is null)
            return new StoreResult.NotFound($"no task {id}");

        if (row.State == TaskState.Parked)
        {
            if (row.TeamId != lead.Team.Value)
                return new StoreResult.Rejected(Rule.ActorLacksAuthority,
                    "input requests are answered by the Lead or a human");
            return await RunTransition(row, new WakeParked(), ct);
        }

        // §11: build the redispatch park record from what the plane holds — the
        // machine still holding the lease (preferred for transcript resume) and the
        // session ref stamped from this work session's SessionStartedEvent. Same
        // shape the wait-TTL sweeper writes; the working directory originates
        // runner-side and is null here. Null when the machine is gone, so redispatch
        // cold-starts elsewhere from the workspace (§11). Ignored by the engine on
        // any non-blocked state (the fall-through wrong-state rejection).
        var park = leaseMachine is { } machine
            ? new ParkRecord(machine, Directory: null, HarnessSessionRef: row.HarnessSessionRef, row.Attempt)
            : null;
        return await RunTransition(row, new AnswerInput(lead, park), ct);
    }

    /// <summary>
    /// The dispatch transaction and the one raw-SQL path (§3.1). Selects a
    /// single eligible submitted task with FOR UPDATE SKIP LOCKED so
    /// concurrent dispatchers never pick the same row (§9 check 5), then runs
    /// the Dispatch transition — which re-checks readiness, back-pressure, and
    /// profile as defense in depth.
    /// </summary>
    public async Task<StoreResult> DispatchNextAsync(
        MachineSnapshot machine, WorkerInstanceId newInstance, CancellationToken ct = default)
    {
        if (!machine.Ready || machine.UnderBackPressure)
            return new StoreResult.NotFound($"machine {machine.MachineId} is not accepting dispatch");

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Lock a single eligible row's id with SKIP LOCKED, then load the
        // entity by id inside the same transaction. Selecting only the id
        // (not SELECT *) keeps the xmin concurrency token out of the raw query;
        // the row lock is held to end of transaction, so concurrent dispatchers
        // skip it. Profile match is the SQL half of check 5: a task with no
        // profile runs anywhere; a task with one runs only where the machine
        // declares it.
        var profiles = machine.DeclaredProfiles.ToArray();
        var claimedId = await db.Database
            .SqlQuery<Guid>(
                $"""
                 SELECT id AS "Value" FROM tasks
                 WHERE state = 'Submitted'
                   AND (profile IS NULL OR profile = ANY({profiles}))
                 ORDER BY id
                 FOR UPDATE SKIP LOCKED
                 LIMIT 1
                 """)
            .FirstOrDefaultAsync(ct);

        if (claimedId == Guid.Empty)
            return new StoreResult.NotFound("no eligible submitted task");

        var claimed = await db.Tasks.FirstAsync(t => t.Id == claimedId, ct);

        var result = await RunTransition(claimed, new Dispatch(machine, newInstance), ct, tx);
        if (result is StoreResult.Applied applied)
        {
            await tx.CommitAsync(ct);
            // Surface the row's opaque transport metadata so DispatchService can act
            // on it (neither reaches the engine): the trace context parents the
            // dispatch span on the Lead's create_task trace, and the harness session
            // ref — present when the task was worked before and parked/requeued —
            // rides the DispatchCommand back so the runner can resume the transcript
            // (§11). Null on a task dispatched for the first time.
            return applied with
            {
                TraceContext = claimed.TraceContext,
                HarnessSessionRef = claimed.HarnessSessionRef,
            };
        }
        return result;
    }

    /// <summary>
    /// Records a live endpoint for a working task (§8.2). Only the incumbent
    /// worker of a task that is currently working may register — the same
    /// authority check as a state transition, though registration is not one.
    /// "Register after a successful bind" is the worker's discipline (§8.2);
    /// the store only records what it's told.
    /// </summary>
    public async Task<StoreResult> RegisterServiceAsync(
        WorkerCaller caller, string name, int port, CancellationToken ct = default)
    {
        var row = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == caller.Task.Value, ct);
        if (row is null)
            return new StoreResult.NotFound($"no task {caller.Task}");
        if (row.State != TaskState.Working)
            return new StoreResult.Rejected(Rule.InvalidSourceState,
                $"services register only while working, not {row.State}");
        if (row.TeamId != caller.Team.Value || row.CurrentInstanceId != caller.Instance.Value)
            return new StoreResult.Rejected(Rule.IncumbentInstanceOnly,
                "only the incumbent worker of this task may register a service");

        db.RegisteredServices.Add(new RegisteredServiceRow
        {
            TaskId = caller.Task.Value,
            TeamId = caller.Team.Value,
            Name = name,
            Port = port,
            CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        return new StoreResult.Applied(row.ToDomain(), []);
    }

    /// <summary>
    /// The worker's own assignment (§7, worker-skill.md): the prose description,
    /// completion criteria, workspace, namespace, and attempt count a dispatched
    /// worker reads before starting. A pure read gated by the same authority as a
    /// worker transition — returned <b>only</b> for the caller's own task and
    /// <b>only</b> while the caller is that task's incumbent instance (the
    /// RegisterServiceAsync gate, §9 check 14). Anything else returns null, so a
    /// zombie or a cross-task token learns nothing — never another task's content.
    /// </summary>
    public async Task<WorkerAssignment?> GetAssignmentAsync(WorkerCaller caller, CancellationToken ct = default)
    {
        var row = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == caller.Task.Value, ct);
        if (row is null)
            return null;
        if (row.TeamId != caller.Team.Value || row.CurrentInstanceId != caller.Instance.Value)
            return null;

        return new WorkerAssignment(
            row.Namespace, row.Description, row.CompletionCriteria, row.Workspace, row.Attempt);
    }

    /// <summary>
    /// The Team view read (§10, §12): task counts by state plus a per-task
    /// structural summary, scoped to one Team. A pure read — it runs no
    /// transition and returns no prose (§10). The caller's Team comes from its
    /// lead claim, never a parameter, so a Lead only ever sees its own Team.
    /// </summary>
    public async Task<TeamStateView> GetTeamStateAsync(TeamId team, CancellationToken ct = default)
    {
        var rows = await db.Tasks.AsNoTracking()
            .Where(t => t.TeamId == team.Value)
            .Select(t => new
            {
                t.Id,
                t.Namespace,
                t.State,
                t.CompletionMode,
                t.Attempt,
                Parked = t.ParkMachine != null,
            })
            .ToListAsync(ct);

        var counts = rows
            .GroupBy(t => t.State)
            .ToDictionary(g => g.Key, g => g.Count());

        var summaries = rows
            .Select(t => new TeamTaskSummary(t.Id, t.Namespace, t.State, t.CompletionMode, t.Attempt, t.Parked))
            .ToList();

        return new TeamStateView(team.Value, rows.Count, counts, summaries);
    }

    /// <summary>
    /// The verifier's poll (§5, §10 verifier webhook): every task in
    /// <see cref="TaskState.Verifying"/> with an <see cref="CompletionMode.Automated"/>
    /// completion mode, so an automated verifier can find the tasks it may rule on
    /// and fetch each one's result reference. Review-mode tasks are deliberately
    /// omitted — their verdict comes through the Lead's <c>submit_review</c>
    /// (human-confirmed, §7), not this path. A cross-Team read by design: the
    /// verifier is an Instance-scoped credential (§5), not attached to any Team, so
    /// it sees every automated task awaiting its check. A pure read — no transition,
    /// and the only prose it returns is the completion criteria §5 explicitly grants.
    /// </summary>
    public async Task<IReadOnlyList<VerifyingTaskView>> ListVerifyingAsync(CancellationToken ct = default) =>
        await db.Tasks.AsNoTracking()
            .Where(t => t.State == TaskState.Verifying && t.CompletionMode == CompletionMode.Automated)
            .OrderBy(t => t.Namespace)
            .Select(t => new VerifyingTaskView(t.Id, t.Namespace, t.CompletionCriteria, t.ResultReference))
            .ToListAsync(ct);

    /// <summary>
    /// The wait-TTL sweeper's poll (§11): every task in
    /// <see cref="TaskState.BlockedOnInput"/> with the timestamp it entered that
    /// state (<see cref="TaskRow.BlockedAt"/>) and its current attempt. A pure
    /// read — no transition, no prose. The sweeper ages <c>BlockedAt</c> against
    /// the configured wait TTL and cross-references the connection registry for
    /// the dispatched machine's liveness, then expresses the outcome as a
    /// <see cref="WaitTtlExpired"/> or <see cref="LivenessLost"/> command through
    /// the store — the engine, never raw SQL, owns every transition (§15).
    /// </summary>
    public async Task<IReadOnlyList<BlockedTaskView>> ListBlockedAsync(CancellationToken ct = default) =>
        await db.Tasks.AsNoTracking()
            .Where(t => t.State == TaskState.BlockedOnInput)
            .OrderBy(t => t.BlockedAt)
            .Select(t => new BlockedTaskView(t.Id, t.BlockedAt, t.Attempt, t.HarnessSessionRef))
            .ToListAsync(ct);

    /// <summary>
    /// A pure read of a task's current state, or null if it does not exist. The
    /// dispatch loop and the runner event sink read state to decide whether an
    /// inbound runner event still bears on a working task — e.g. an
    /// <c>exited</c> after the worker already reported result is moot (§10).
    /// </summary>
    public async Task<TaskState?> GetStateAsync(TaskId id, CancellationToken ct = default) =>
        await db.Tasks.AsNoTracking()
            .Where(t => t.Id == id.Value)
            .Select(t => (TaskState?)t.State)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Stamps the opaque harness session ref onto a task row (§11 resume), from a
    /// <see cref="Docket.Contracts.SessionStartedEvent"/> the runner event sink
    /// received. Not a state transition — this is transport metadata the plane
    /// never interprets (like <see cref="TaskRow.ResultReference"/>/<c>TraceContext</c>),
    /// so it is a targeted set-based write that runs no engine transition, takes no
    /// xmin token, and fires no NOTIFY — exactly the shape of the token-revoke
    /// effect. Latest write wins: a resumed or restarted session overwrites the ref
    /// so a subsequent park carries the current session. A no-op row count when the
    /// task no longer exists, which is fine — the ref is only ever read on dispatch.
    /// </summary>
    public async Task StampHarnessSessionRefAsync(TaskId id, string sessionRef, CancellationToken ct = default) =>
        await db.Tasks
            .Where(t => t.Id == id.Value)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.HarnessSessionRef, sessionRef), ct);

    /// <summary>
    /// Records a runner <c>auth-failed</c> event (§11) as a first-class task event
    /// row — the structured operation/target/error-code/missing-scope facts the
    /// runner reported — so the §12 dashboard can surface it (remediation menu
    /// rendering itself stays deferred, #50 persists; #54-adjacent renders it).
    /// Not a state transition: an auth failure does not move the task through §6,
    /// so this appends an event row with no from/to state, runs no engine
    /// transition, and takes no xmin token — but fires the same NOTIFY a transition
    /// does, so a listening dashboard wakes on it. The event carries only a task id
    /// (§10), so the owning Team is resolved from the row; a no-op when the task no
    /// longer exists, which is fine — the row is only ever read by the dashboard.
    /// </summary>
    public async Task RecordAuthFailureAsync(
        TaskId task, string operation, string target, string errorCode, string? missingScope,
        CancellationToken ct = default)
    {
        var teamId = await db.Tasks.AsNoTracking()
            .Where(t => t.Id == task.Value)
            .Select(t => (Guid?)t.TeamId)
            .FirstOrDefaultAsync(ct);
        if (teamId is null)
            return;

        db.TaskEvents.Add(new TaskEventRow
        {
            TaskId = task.Value,
            TeamId = teamId.Value,
            Kind = TaskEventRow.AuthFailedKind,
            AuthOperation = operation,
            AuthTarget = target,
            AuthErrorCode = errorCode,
            AuthMissingScope = missingScope,
            OccurredAt = clock.GetUtcNow(),
        });
        await CommitAsync(task.Value, ct);
    }

    /// <summary>
    /// Records a runner <c>subagent-spawned</c> event (§10/§12) as a first-class
    /// task event row so the dashboard shows it as a progress signal (subagent
    /// lineage). Same out-of-band shape as <see cref="RecordAuthFailureAsync"/>:
    /// no transition, no xmin token, one NOTIFY. The agent ids are progressive
    /// enhancement — both null when the harness reports no lineage (§10) — so they
    /// are stored verbatim, nulls and all. A no-op when the task is gone.
    /// </summary>
    public async Task RecordSubagentSpawnAsync(
        TaskId task, string? agentId, string? parentAgentId, CancellationToken ct = default)
    {
        var teamId = await db.Tasks.AsNoTracking()
            .Where(t => t.Id == task.Value)
            .Select(t => (Guid?)t.TeamId)
            .FirstOrDefaultAsync(ct);
        if (teamId is null)
            return;

        db.TaskEvents.Add(new TaskEventRow
        {
            TaskId = task.Value,
            TeamId = teamId.Value,
            Kind = TaskEventRow.SubagentSpawnedKind,
            SubagentId = agentId,
            SubagentParentId = parentAgentId,
            OccurredAt = clock.GetUtcNow(),
        });
        await CommitAsync(task.Value, ct);
    }

    private async Task<StoreResult> RunTransition(
        TaskRow row, TaskCommand command, CancellationToken ct,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? outerTx = null)
    {
        var before = row.State;
        var result = TaskStateMachine.Apply(row.ToDomain(), command);
        if (result is TransitionResult.Rejected r)
            return new StoreResult.Rejected(r.Rule, r.Reason);

        var ok = (TransitionResult.Transitioned)result;
        row.CopyFrom(ok.Task);
        // #23, §7: the reported result reference is opaque content the store
        // captures on the working → verifying transition. The engine and
        // TaskRecord stay content-free (the reference never lands on the pure
        // state), and CopyFrom deliberately does not carry it — so a succeeding
        // ReportResult is the one place the row's ResultReference is written,
        // where the verifier's read scope (§5) later fetches it.
        if (command is ReportResult reported)
            row.ResultReference = reported.ResultReference;
        // §11 wait-TTL: stamp when the task entered blocked_on_input so the sweeper
        // can age its wait deadline, and clear it on the way out. Opaque plane
        // plumbing captured here (like ResultReference above), never an engine field —
        // the pure state machine holds no clock (§6: timers live in the control plane).
        // The same working → blocked_on_input transition also carries the typed
        // request kind onto its event row (§10/§12, #50) so the dashboard can render
        // what kind of attention the task needs — the kind is command content the
        // engine already requires, threaded through here, not onto the pure state.
        InputRequestKind? inputKind = null;
        if (command is RequestInput ri && row.State == TaskState.BlockedOnInput)
        {
            row.BlockedAt = clock.GetUtcNow();
            inputKind = ri.Kind;
        }
        else if (before == TaskState.BlockedOnInput && row.State != TaskState.BlockedOnInput)
            row.BlockedAt = null;
        ApplyEffects(row, ok.Effects);
        AppendEvent(row.Id, row.TeamId, command.GetType().Name, before, row.State,
            detail: DescribeEffects(ok.Effects), inputKind: inputKind);

        try
        {
            await CommitAsync(row.Id, ct, outerTx);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new StoreResult.Conflict($"task {row.Id} moved concurrently; re-read and retry");
        }

        return new StoreResult.Applied(ok.Task, ok.Effects);
    }

    private void ApplyEffects(TaskRow row, IReadOnlyList<Effect> effects)
    {
        foreach (var effect in effects)
        {
            switch (effect)
            {
                case MintWorkerInstanceToken mint:
                    db.WorkerInstances.Add(new WorkerInstanceRow
                    {
                        Id = mint.Instance.Value,
                        TaskId = row.Id,
                        Revoked = false,
                        CreatedAt = clock.GetUtcNow(),
                    });
                    break;

                case RevokeWorkerInstanceToken revoke:
                    // Set-based: no need to materialize the row to revoke it.
                    db.WorkerInstances
                        .Where(w => w.Id == revoke.Instance.Value && !w.Revoked)
                        .ExecuteUpdate(s => s
                            .SetProperty(w => w.Revoked, true)
                            .SetProperty(w => w.RevokedAt, clock.GetUtcNow()));
                    break;

                case ClearServicesAndForwards:
                    db.RegisteredServices.Where(s => s.TaskId == row.Id).ExecuteDelete();
                    // §8.3: leaving working also releases the task's relay forwards.
                    // Revoke every live grant this task produced, so a grant issued
                    // against a now-gone service can never open a tunnel — the same
                    // moment its registered services are cleared, no schema churn.
                    // Established splices are untouched; a grant only gates open.
                    db.RelayGrants
                        .Where(g => g.ProducerTaskId == row.Id && !g.Revoked)
                        .ExecuteUpdate(s => s.SetProperty(g => g.Revoked, true));
                    break;

                // WriteParkRecord is already reflected by CopyFrom (row park columns).
                // DiscardWorkspace / DeferWorkspaceDiscardUntilVerdict are docketd's
                // to enact; recorded in the event detail for the daemon to observe.
                case WriteParkRecord:
                case DiscardWorkspace:
                case DeferWorkspaceDiscardUntilVerdict:
                    break;
            }
        }
    }

    private void AppendEvent(
        Guid taskId, Guid teamId, string kind, TaskState? from, TaskState to, string? detail,
        InputRequestKind? inputKind = null)
        => db.TaskEvents.Add(new TaskEventRow
        {
            TaskId = taskId,
            TeamId = teamId,
            Kind = kind,
            FromState = from,
            ToState = to,
            Detail = detail,
            InputKind = inputKind,
            OccurredAt = clock.GetUtcNow(),
        });

    private async Task CommitAsync(
        Guid taskId, CancellationToken ct,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? outerTx = null)
    {
        var ownTx = outerTx is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        try
        {
            await db.SaveChangesAsync(ct);
            // NOTIFY in the same transaction: subscribers wake only on committed
            // writes, and never on a rolled-back one.
            await db.Database.ExecuteSqlAsync(
                $"SELECT pg_notify({DocketDbContext.EventChannel}, {taskId.ToString()})", ct);
            if (ownTx is not null)
                await ownTx.CommitAsync(ct);
        }
        finally
        {
            if (ownTx is not null)
                await ownTx.DisposeAsync();
        }
    }

    private static string DescribeEffects(IReadOnlyList<Effect> effects) =>
        effects.Count == 0 ? "" : string.Join(",", effects.Select(e => e.GetType().Name));
}
