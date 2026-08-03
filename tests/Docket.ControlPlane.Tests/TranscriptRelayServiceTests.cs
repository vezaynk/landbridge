using Docket.Contracts;
using Docket.ControlPlane.Auth;
using Docket.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Docket.ControlPlane.Tests;

/// <summary>
/// The §12 read path on the plane side: the terminal-state gate, the offline/wedged-machine
/// answers that must never present as a hang, and relaying one range without keeping a byte
/// of it. A fake connection stands in for the socket — it answers <c>read-transcript</c>
/// through the real <see cref="RunnerEventSink"/>, exactly as a real docketd would.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TranscriptRelayServiceTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string Machine = "mac-1";

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── The gate (§12/§13: verbatim serving is scoped, not filtered) ───────────

    [SkippableFact]
    public async Task A_completed_task_is_readable()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var rig = Rig();
        var task = await SeedTerminalTaskAsync(rig);
        rig.AnswerWith(command => new TranscriptChunkEvent(
            command.Task, command.RequestId, Text: "verbatim bytes\n", NextOffset: 15, Eof: true));

        var result = await rig.Relay.ReadAsync(task, Machine, ordinal: 1, TranscriptStreams.Stdout, offset: 0);

        var range = Assert.IsType<TranscriptResult.Range>(result);
        Assert.Equal("verbatim bytes\n", range.Text);
        Assert.True(range.Eof);
    }

    [SkippableTheory]
    [InlineData(TaskState.Submitted)]
    [InlineData(TaskState.Working)]
    [InlineData(TaskState.Verifying)]
    public async Task A_task_that_can_still_run_is_refused(TaskState state)
    {
        // The compensating control for verbatim serving is scope: only a task that can never
        // run again is readable (§12/§13, redaction unresolved — §16 open question 8).
        //
        // `verifying` is the load-bearing case and is deliberately EXCLUDED. Unlike every
        // other transition out of working, report_result does NOT emit
        // RevokeWorkerInstanceToken (TaskStateMachine.ApplyReportResult) — the revoke waits
        // for the verdict — so a verifying task's transcript can still carry a LIVE dkt_w_
        // token that would be replayable by anyone who read it. Do not widen this to
        // verifying without changing that, however tempting it is to let a reviewer read the
        // transcript of the thing they are reviewing.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var rig = Rig();
        var task = await SeedTaskInStateAsync(rig, state);
        rig.AnswerWith(_ => throw new InvalidOperationException("the gate must refuse before any command is sent"));

        var result = await rig.Relay.ReadAsync(task, Machine, ordinal: 1, TranscriptStreams.Stdout, offset: 0);

        var unavailable = Assert.IsType<TranscriptResult.Unavailable>(result);
        Assert.Equal(TranscriptUnavailable.NotTerminal, unavailable.Reason);
        Assert.Empty(rig.Sent); // the gate runs before the machine is even consulted
    }

    [SkippableFact]
    public async Task An_unknown_task_is_refused_without_touching_a_machine()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var rig = Rig();

        var result = await rig.Relay.ListAsync(TaskId.New(), Machine);

        Assert.Equal(TranscriptUnavailable.NoSuchTask, Assert.IsType<TranscriptResult.Unavailable>(result).Reason);
        Assert.Empty(rig.Sent);
    }

    // ── Failure modes that must not hang ──────────────────────────────────────

    [SkippableFact]
    public async Task An_offline_machine_is_a_clear_answer_not_a_hang()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var rig = Rig(registerMachine: false);
        var task = await SeedTerminalTaskAsync(rig);

        var result = await rig.Relay.ListAsync(task, Machine);

        var unavailable = Assert.IsType<TranscriptResult.Unavailable>(result);
        Assert.Equal(TranscriptUnavailable.MachineOffline, unavailable.Reason);
        // The operator needs to know WHY there is nothing to read: the bytes live only there.
        Assert.Contains("not connected", unavailable.Detail);
    }

    [SkippableFact]
    public async Task A_machine_that_never_answers_times_out_rather_than_hanging()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        // A docketd predating read-transcript rejects it at the wire boundary and never
        // replies — indistinguishable from a wedged machine, so both must time out.
        var rig = Rig();
        var task = await SeedTerminalTaskAsync(rig);
        rig.AnswerWith(_ => null); // received, deliberately unanswered

        var read = await AdvanceUntilDoneAsync(rig.Clock, rig.Relay.ListAsync(task, Machine));

        var unavailable = Assert.IsType<TranscriptResult.Unavailable>(read);
        Assert.Equal(TranscriptUnavailable.Timeout, unavailable.Reason);
        Assert.Equal(0, rig.Waiters.OutstandingCount); // the waiter is deregistered, not leaked
    }

    [SkippableFact]
    public async Task A_machine_refusal_is_relayed_as_an_operator_facing_reason()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var rig = Rig();
        var task = await SeedTerminalTaskAsync(rig);
        rig.AnswerWith(command => new TranscriptChunkEvent(
            command.Task, command.RequestId, Refusal: TranscriptRefusals.NoTranscript));

        var result = await rig.Relay.ListAsync(task, Machine);

        var unavailable = Assert.IsType<TranscriptResult.Unavailable>(result);
        Assert.Equal(TranscriptUnavailable.MachineRefused, unavailable.Reason);
        // The plane never invents a reason; it turns the machine's vocabulary into a sentence.
        Assert.Contains("holds no transcript", unavailable.Detail);
    }

    [SkippableFact]
    public async Task An_inventory_reply_is_surfaced_as_the_instances_the_machine_holds()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var rig = Rig();
        var task = await SeedTerminalTaskAsync(rig);
        rig.AnswerWith(command => new TranscriptChunkEvent(
            command.Task, command.RequestId, Eof: true,
            Instances: [new TranscriptInstance(1, 4096, 128, DateTimeOffset.UtcNow)]));

        var result = await rig.Relay.ListAsync(task, Machine);

        var inventory = Assert.IsType<TranscriptResult.Inventory>(result);
        Assert.Equal(Machine, inventory.Machine);
        Assert.Equal(4096, Assert.Single(inventory.Instances).StdoutBytes);
        // Ordinal 0 is the inventory request (§12).
        Assert.Equal(0, Assert.IsType<ReadTranscriptCommand>(Assert.Single(rig.Sent)).Ordinal);
    }

    [SkippableFact]
    public async Task A_second_read_to_the_same_machine_is_refused_while_one_is_in_flight()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        // Single-flight per machine: a transcript read is an operator convenience riding the
        // same socket as dispatch and liveness, so a refresh must not multiply its cost.
        var rig = Rig();
        var task = await SeedTerminalTaskAsync(rig);
        rig.AnswerWith(_ => null); // hold the first read open

        var first = rig.Relay.ListAsync(task, Machine);
        await WaitUntilAsync(() => rig.Sent.Count == 1);
        var second = await rig.Relay.ListAsync(task, Machine);

        Assert.Equal(TranscriptUnavailable.Busy, Assert.IsType<TranscriptResult.Unavailable>(second).Reason);
        Assert.Single(rig.Sent);

        // And the gate reopens once the first finishes.
        await AdvanceUntilDoneAsync(rig.Clock, first);
        rig.AnswerWith(command => new TranscriptChunkEvent(command.Task, command.RequestId, Eof: true, Instances: []));
        Assert.IsType<TranscriptResult.Inventory>(await rig.Relay.ListAsync(task, Machine));
    }

    // ── Rig ───────────────────────────────────────────────────────────────────

    /// <summary>Polls a condition on the real clock — the fake clock drives the relay's
    /// timeout, so waiting on it here would deadlock.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && !condition())
            await Task.Delay(10);
        Assert.True(condition(), "condition did not become true within 5s");
    }

    /// <summary>
    /// Drives the fake clock until <paramref name="read"/> finishes. Advancing once is a
    /// race: the relay registers its timeout only after the command has been sent, and a
    /// fake clock that already jumped past the deadline never fires for a timer registered
    /// afterwards — the read would then wait forever. Nudging repeatedly is race-free.
    /// </summary>
    private static async Task<TranscriptResult> AdvanceUntilDoneAsync(
        Microsoft.Extensions.Time.Testing.FakeTimeProvider clock, Task<TranscriptResult> read)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!read.IsCompleted && DateTime.UtcNow < deadline)
        {
            clock.Advance(TranscriptRelayService.RangeTimeout + TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }
        return await read;
    }

    private Harness Rig(bool registerMachine = true)
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
            new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));
        var registry = new RunnerConnectionRegistry(clock);
        var waiters = new TranscriptWaiters();
        var scopes = ScopeFactory(clock);
        var sink = new RunnerEventSink(scopes, registry, new ForwardWaiters(), waiters, NullLogger<RunnerEventSink>.Instance);
        var harness = new Harness
        {
            Clock = clock,
            Registry = registry,
            Waiters = waiters,
            Scopes = scopes,
            Relay = new TranscriptRelayService(
                scopes, registry, waiters, NullLogger<TranscriptRelayService>.Instance, clock),
        };

        if (registerMachine)
        {
            registry.Register(Machine, new HashSet<string> { "default" }, async (command, ct) =>
            {
                if (command is not ReadTranscriptCommand read)
                    return;
                harness.Sent.Add(read);
                // The machine's reply comes back through the real sink, so the test exercises
                // the same completion path production uses.
                if (harness.Answer?.Invoke(read) is { } reply)
                    await sink.HandleAsync(reply, ct);
            });
            // Ready + a live snapshot, which is what the relay's connectivity check reads.
            registry.ApplyHeartbeat(Machine, new MachineHeartbeat(
                Machine, Ready: true, UnderBackPressure: false, new SystemLoad(0, 0, 0),
                RunningTasks: 0, Profiles: ["default"], At: clock.GetUtcNow(), TranscriptsServable: true));
        }

        return harness;
    }

    private sealed class Harness
    {
        public required Microsoft.Extensions.Time.Testing.FakeTimeProvider Clock { get; init; }
        public required RunnerConnectionRegistry Registry { get; init; }
        public required TranscriptWaiters Waiters { get; init; }
        public required IServiceScopeFactory Scopes { get; init; }
        public required TranscriptRelayService Relay { get; init; }

        public List<ReadTranscriptCommand> Sent { get; } = [];
        public Func<ReadTranscriptCommand, TranscriptChunkEvent?>? Answer { get; private set; }

        /// <summary>How the fake machine answers; returning null models "received, never
        /// answered" (a wedged machine, or one whose docketd rejects the command).</summary>
        public void AnswerWith(Func<ReadTranscriptCommand, TranscriptChunkEvent?> answer) => Answer = answer;
    }

    private IServiceScopeFactory ScopeFactory(TimeProvider clock)
    {
        var services = new ServiceCollection();
        services.AddDbContext<DocketDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        services.AddScoped<TaskStore>();
        services.AddScoped<TokenService>();
        services.AddSingleton(clock);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>Create → dispatch → report → accept: a completed task, the common case for
    /// reading a transcript after the fact.</summary>
    private async Task<TaskId> SeedTerminalTaskAsync(Harness rig) =>
        await SeedTaskInStateAsync(rig, TaskState.Completed);

    /// <summary>Drives a real task to <paramref name="state"/> through the state machine, so
    /// the gate is tested against states the engine actually produces.</summary>
    private async Task<TaskId> SeedTaskInStateAsync(Harness rig, TaskState state)
    {
        var team = TeamId.New();
        var lead = new LeadClaim(team);
        await using var db = pg.NewContext();
        var store = new TaskStore(db, rig.Clock);

        var created = (StoreResult.Applied)await store.CreateAsync(new CreateTask(
            lead, team, "completion criteria", CompletionMode.Lead, Profile: null, TeamBudgetRemains: true));
        var id = created.Task.Id;
        if (state == TaskState.Submitted)
            return id;

        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(
            new MachineSnapshot(Machine, Ready: true, UnderBackPressure: false, new HashSet<string> { "default" }),
            instance);
        if (state == TaskState.Working)
            return id;

        await store.ApplyAsync(id, new ReportResult(new WorkerCaller(team, id, instance), "result-ref"));
        if (state == TaskState.Verifying)
            return id;

        await store.ApplyAsync(id, state switch
        {
            TaskState.Completed => new VerdictAccept(lead),
            TaskState.Canceled => new Cancel(new HumanSession(), CancelDisposition.Preserve),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "unsupported seed state"),
        });
        return id;
    }
}
