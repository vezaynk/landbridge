using Docket.Contracts;
using Docket.ControlPlane;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Runner;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using HarnessProgram = Docket.Runner.TestHarness.Program;

namespace Docket.Mcp.Tests;

/// <summary>
/// §11 resume crown, skeleton-style (a real spawned harness, no LLM): proves the
/// opaque harness session ref round-trips <b>row → park → dispatch → session/load</b>
/// through the real plane pieces wired together —
/// <see cref="ProcessSupervisor"/> handshakes a real ACP agent, the
/// <see cref="RunnerEventSink"/> stamps the session id, the
/// <see cref="WaitTtlSweeper"/> parks with it, and redispatch hands it back so the
/// supervisor <c>session/load</c>s that id.
///
/// <para>Deviations from a live docketd, called out where they matter: the pieces
/// are wired directly rather than over the <c>/runner</c> WebSocket (the wire is
/// covered by RunnerSpineEndToEndTests + the contract round-trips), and the first
/// harness is left alive through the park so the task stays tracked for the sweeper
/// — mirroring WaitTtlSweeperTests, since re-proving park-tracking mechanics is not
/// this crown's job. A single <see cref="FakeTimeProvider"/> drives the wait TTL.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ResumeTranscriptEndToEndTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>The worker's ask and the Lead's answer (§11), carried through the same
    /// park→wake→redispatch loop as the session ref. Distinctive strings so the argv
    /// assertion below is meaningful.</summary>
    private const string Question = "which database should I target, staging-pg or the local docker one?";
    private const string Answer = "target staging-pg; the docker one has no seed data.";

    [SkippableFact]
    public async Task Session_ref_round_trips_from_row_through_park_to_the_resumed_spawn_argv()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var clock = new FakeTimeProvider();
        var scopes = ScopeFactory(clock);
        var workRoot = NewWorkRoot();
        var ring = new OutboundEventRing(capacity: 256);
        var registry = new RunnerConnectionRegistry(clock);
        var supervisor = new ProcessSupervisor(
            new MachineConfig(workRoot, TimeSpan.FromSeconds(15), BackPressureThresholds.Default), ring, clock);
        var sink = new RunnerEventSink(scopes, registry, new ForwardWaiters(), new TranscriptWaiters(), new ProcessControlRelay(registry), NullLogger<RunnerEventSink>.Instance);
        var profile = ResumeProfile(); // emit-stream (Terminal) cold + echo-argv resume

        // A drain loop routes the supervisor's outbound events into the sink exactly
        // as the socket loop would in production. Stopped before the resume spawn.
        var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var drainLoop = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in ring.ReadAllAsync(drainCts.Token))
                    await sink.HandleAsync(item.Event, drainCts.Token);
            }
            catch (OperationCanceledException) { /* stopped on teardown */ }
        }, CancellationToken.None);

        try
        {
            var team = TeamId.New();
            var snapshot = new MachineSnapshot("m1", Ready: true, UnderBackPressure: false, Set("default"));

            // ── Create + initial dispatch (cold: no resume ref yet) ─────────────
            TaskId taskId;
            await using (var db = pg.NewContext())
            {
                var store = new TaskStore(db, clock);
                var created = (StoreResult.Applied)await store.CreateAsync(
                    new CreateTask(new LeadClaim(team), team, "criteria", CompletionMode.Lead, null), ct);
                taskId = created.Task.Id;
            }

            registry.Register("m1", Set("default"), (_, _) => Task.CompletedTask);
            registry.ApplyHeartbeat("m1", Heartbeat("m1", "default")); // ready, stamped at t0

            WorkerInstanceId instance1;
            await using (var db = pg.NewContext())
            {
                var store = new TaskStore(db, clock);
                instance1 = WorkerInstanceId.New();
                var applied = (StoreResult.Applied)await store.DispatchNextAsync(snapshot, instance1, ct);
                // First dispatch: nothing worked before, so no session ref surfaces.
                Assert.Null(applied.HarnessSessionRef);
            }
            registry.TrackDispatch("m1", taskId);

            var coldDispatch = new DispatchCommand(
                taskId, "default", WorkerToken: "worker-1",
                McpConfigJson: """{"mcpServers":{}}""", ResumeSessionRef: null);
            supervisor.Spawn(coldDispatch, profile, "m1");

            // ── The harness reports its session id → sink stamps the row ────────
            Assert.True(
                await WaitUntilAsync(async () => !string.IsNullOrWhiteSpace(await HarnessSessionRefAsync(clock, taskId)), TimeSpan.FromSeconds(30)),
                "the harness session ref never reached the task row");
            var stampedRef = await HarnessSessionRefAsync(clock, taskId);

            // ── Block on input, then let the wait TTL expire → park ─────────────
            await using (var db = pg.NewContext())
            {
                var store = new TaskStore(db, clock);
                var caller = new WorkerCaller(team, taskId, instance1);
                Assert.IsType<StoreResult.Applied>(
                    await store.ApplyAsync(taskId, new RequestInput(caller, InputRequestKind.Question, Question), ct));
            }

            var sweeper = new WaitTtlSweeper(
                scopes, registry, clock, NullLogger<WaitTtlSweeper>.Instance,
                waitTtl: TimeSpan.FromMinutes(30), machineLivenessWindow: TimeSpan.FromHours(2));
            clock.Advance(TimeSpan.FromMinutes(31));
            await sweeper.SweepAsync(ct);

            await using (var db = pg.NewContext())
            {
                var row = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId.Value, ct);
                Assert.Equal(TaskState.Parked, row.State);
                Assert.Equal("m1", row.ParkMachine);
                // The crown's spine: the harness session ref survives the park on the row
                // dispatch reads it from.
                Assert.Equal(stampedRef, row.HarnessSessionRef);
            }

            // ── The awaited answer lands → wake to submitted ────────────────────
            // The Lead answers in words. The task is already parked, so this takes the
            // WakeParked half of the one-call path — the answer must survive that
            // branch too, or a Lead loses its answer to a race with the sweeper (§11).
            await using (var db = pg.NewContext())
            {
                var store = new TaskStore(db, clock);
                Assert.IsType<StoreResult.Applied>(
                    await store.AnswerOrWakeAsync(new LeadClaim(team), taskId, leaseMachine: null, Answer, ct));
            }

            // Stop routing events, then retire the first harness. With the drain
            // stopped its exit can't race the redispatch back onto submitted.
            await drainCts.CancelAsync();
            await drainLoop;
            supervisor.Kill(taskId);
            Assert.True(await WaitUntilAsync(() => Task.FromResult(supervisor.RunningTotal == 0), TimeSpan.FromSeconds(15)),
                "the first harness never exited");

            // ── Redispatch: the stored ref surfaces and rides the command ───────
            WorkerInstanceId instance2;
            string? resumeRef;
            await using (var db = pg.NewContext())
            {
                var store = new TaskStore(db, clock);
                instance2 = WorkerInstanceId.New();
                var applied = (StoreResult.Applied)await store.DispatchNextAsync(snapshot, instance2, ct);
                resumeRef = applied.HarnessSessionRef;
                // Dispatch surfaces the ref the first session reported.
                Assert.Equal(stampedRef, resumeRef);

                // §11: the successor's own get_task read carries the answer it was
                // resumed FOR, and the question that gives it meaning — the whole point
                // of the park→answer→redispatch loop. Read through the same store method
                // the worker's get_task tool delegates to, as the new incumbent instance.
                var assignment = await store.GetAssignmentAsync(new WorkerCaller(team, taskId, instance2), ct);
                Assert.NotNull(assignment);
                Assert.Equal(Question, assignment!.Question);
                Assert.Equal(Answer, assignment.Answer);
            }

            var resumeDispatch = new DispatchCommand(
                taskId, "default", WorkerToken: "worker-2",
                McpConfigJson: """{"mcpServers":{}}""", ResumeSessionRef: resumeRef);
            supervisor.Spawn(resumeDispatch, profile, "m1");

            var loadedPath = Path.Combine(workRoot, taskId.ToString(), "acp-loaded");
            Assert.True(await WaitUntilAsync(() => Task.FromResult(File.Exists(loadedPath)), TimeSpan.FromSeconds(15)),
                "the resumed harness never recorded session/load");
            Assert.Equal(stampedRef, (await File.ReadAllTextAsync(loadedPath, ct)).Trim());
            Assert.DoesNotContain(Answer, await File.ReadAllTextAsync(loadedPath, ct), StringComparison.Ordinal);
            Assert.DoesNotContain(Question, await File.ReadAllTextAsync(loadedPath, ct), StringComparison.Ordinal);
        }
        finally
        {
            try { supervisor.KillAll(); } catch { /* best effort */ }
            ring.Complete();
            if (!drainCts.IsCancellationRequested)
                await drainCts.CancelAsync();
            try { await drainLoop; } catch { /* best effort */ }
            TryDeleteRoot(workRoot);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private IServiceScopeFactory ScopeFactory(TimeProvider clock)
    {
        var services = new ServiceCollection();
        services.AddDbContext<DocketDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        services.AddDocketStore();
        services.AddSingleton(clock);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private async Task<string?> HarnessSessionRefAsync(TimeProvider clock, TaskId id)
    {
        await using var db = pg.NewContext();
        return await db.Tasks.AsNoTracking()
            .Where(t => t.Id == id.Value)
            .Select(t => t.HarnessSessionRef)
            .SingleAsync();
    }

    /// <summary>The runner test-harness apphost, spawned directly (argv, no shell — §10).</summary>
    private static string HarnessPath()
    {
        var dll = typeof(HarnessProgram).Assembly.Location;
        var dir = Path.GetDirectoryName(dll)!;
        var stem = Path.GetFileNameWithoutExtension(dll);
        var apphost = Path.Combine(dir, OperatingSystem.IsWindows() ? stem + ".exe" : stem);
        return File.Exists(apphost) ? apphost : dll;
    }

    private static ProfileConfig ResumeProfile() =>
        new(
            "default",
            [HarnessPath(), "acp", "emit-stream"],
            new StopConfig(StopMode.Signal, MessageTemplate: null, WindDown: TimeSpan.FromSeconds(30)),
            Resume: null,
            new EventsConfig(EventsSource.None, new Dictionary<string, string>()),
            new TelemetryConfig(Otel: false, Endpoint: null),
            new LogsConfig(),
            MaxConcurrent: null);

    private static string NewWorkRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "docket-resume-crown", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteRoot(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return true;
            await Task.Delay(50);
        }
        return await condition();
    }

    private static IReadOnlySet<string> Set(params string[] names) =>
        new HashSet<string>(names, StringComparer.Ordinal);

    private static MachineHeartbeat Heartbeat(string machineId, params string[] profiles) =>
        new(machineId, Ready: true, UnderBackPressure: false,
            new SystemLoad(0, 0, 0), RunningTasks: 0, profiles, DateTimeOffset.UtcNow);
}
