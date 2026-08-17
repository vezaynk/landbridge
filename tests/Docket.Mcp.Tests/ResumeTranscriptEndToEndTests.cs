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
/// opaque harness session ref round-trips <b>row → park → dispatch → resume argv</b>
/// through the real plane pieces wired together —
/// <see cref="ProcessSupervisor"/> spawns a real harness, the
/// <see cref="RunnerEventSink"/> stamps the ref the harness reports, the
/// <see cref="WaitTtlSweeper"/> parks with it, and redispatch hands it back so the
/// supervisor rebuilds the spawn argv from <c>resume.args</c> with
/// <c>--resume &lt;that id&gt;</c>. The one thing left to an operator spike (§17.0)
/// is a real <c>claude -p --resume</c>; everything up to the resumed argv is here.
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
    public async Task Session_ref_round_trips_from_row_through_park_to_the_resumed_session_load()
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
        var profile = AcpProfile();

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
            SessionId sessionId;
            await using (var db = pg.NewContext())
            {
                var store = new SessionStore(db, clock);
                var created = (StoreResult.Applied)await store.CreateAsync(
                    new CreateSession(new LeadClaim(team), team, "criteria", CompletionMode.Lead, null), ct);
                sessionId = created.Session.Id;
            }

            registry.Register("m1", Set("default"), (_, _) => Task.CompletedTask);
            registry.ApplyHeartbeat("m1", Heartbeat("m1", "default")); // ready, stamped at t0

            WorkerInstanceId instance1;
            await using (var db = pg.NewContext())
            {
                var store = new SessionStore(db, clock);
                instance1 = WorkerInstanceId.New();
                var applied = (StoreResult.Applied)await store.DispatchNextAsync(snapshot, instance1, ct);
                // First dispatch: nothing worked before, so no session ref surfaces.
                Assert.Null(applied.HarnessSessionRef);
            }
            registry.TrackDispatch("m1", sessionId);

            var coldDispatch = new DispatchCommand(
                sessionId, "default", WorkerToken: "worker-1",
                McpConfigJson: """{"mcpServers":{}}""", ResumeSessionRef: null);
            supervisor.Spawn(coldDispatch, profile, "m1");

            // ── The harness reports its session id → sink stamps the row ────────
            Assert.True(
                await WaitUntilAsync(async () => await HarnessSessionRefAsync(clock, sessionId) == HarnessProgram.EmitStreamSessionId, TimeSpan.FromSeconds(30)),
                "the harness session ref never reached the task row");

            // ── Block on input, then let the wait TTL expire → park ─────────────
            await using (var db = pg.NewContext())
            {
                var store = new SessionStore(db, clock);
                var caller = new WorkerCaller(team, sessionId, instance1);
                Assert.IsType<StoreResult.Applied>(
                    await store.ApplyAsync(sessionId, new RequestInput(caller, InputRequestKind.Question, Question), ct));
            }

            var sweeper = new WaitTtlSweeper(
                scopes, registry, clock, NullLogger<WaitTtlSweeper>.Instance,
                waitTtl: TimeSpan.FromMinutes(30), machineLivenessWindow: TimeSpan.FromHours(2));
            clock.Advance(TimeSpan.FromMinutes(31));
            await sweeper.SweepAsync(ct);

            await using (var db = pg.NewContext())
            {
                var row = await db.Sessions.AsNoTracking().SingleAsync(t => t.Id == sessionId.Value, ct);
                Assert.Equal(SessionState.Parked, row.State);
                Assert.Equal("m1", row.ParkMachine);
                // The crown's spine: the harness session ref survives the park on the row
                // dispatch reads it from.
                Assert.Equal(HarnessProgram.EmitStreamSessionId, row.HarnessSessionRef);
            }

            // ── The awaited answer lands → wake to submitted ────────────────────
            // The Lead answers in words. The task is already parked, so this takes the
            // WakeParked half of the one-call path — the answer must survive that
            // branch too, or a Lead loses its answer to a race with the sweeper (§11).
            await using (var db = pg.NewContext())
            {
                var store = new SessionStore(db, clock);
                Assert.IsType<StoreResult.Applied>(
                    await store.AnswerOrWakeAsync(
                        new LeadClaim(team), sessionId, leaseMachine: null, Answer, sessionLive: false, ct));
            }

            // Stop routing events, then retire the first harness. With the drain
            // stopped its exit can't race the redispatch back onto submitted.
            await drainCts.CancelAsync();
            await drainLoop;
            supervisor.Kill(sessionId);
            Assert.True(await WaitUntilAsync(() => Task.FromResult(supervisor.RunningTotal == 0), TimeSpan.FromSeconds(15)),
                "the first harness never exited");

            // ── Redispatch: the stored ref surfaces and rides the command ───────
            WorkerInstanceId instance2;
            string? resumeRef;
            await using (var db = pg.NewContext())
            {
                var store = new SessionStore(db, clock);
                instance2 = WorkerInstanceId.New();
                var applied = (StoreResult.Applied)await store.DispatchNextAsync(snapshot, instance2, ct);
                resumeRef = applied.HarnessSessionRef;
                // Dispatch surfaces the ref the first session reported.
                Assert.Equal(HarnessProgram.EmitStreamSessionId, resumeRef);

                // §11: the successor's own get_session read carries the answer it was
                // resumed FOR, and the question that gives it meaning — the whole point
                // of the park→answer→redispatch loop. Read through the same store method
                // the worker's get_session tool delegates to, as the new incumbent instance.
                var assignment = await store.GetAssignmentAsync(new WorkerCaller(team, sessionId, instance2), ct);
                Assert.NotNull(assignment);
                Assert.Equal(Question, assignment!.Question);
                Assert.Equal(Answer, assignment.Answer);
            }

            var resumeDispatch = new DispatchCommand(
                sessionId, "default", WorkerToken: "worker-2",
                McpConfigJson: """{"mcpServers":{}}""", ResumeSessionRef: resumeRef);
            // The cold spawn already wrote acp_session.json in this same work dir, so clear
            // it first: otherwise the wait below is satisfied by the previous instance's
            // record and the assertion reads a session/new that has nothing to do with the
            // resume under test.
            var sessionRecord = Path.Combine(workRoot, sessionId.ToString(), "acp_session.json");
            File.Delete(sessionRecord);
            supervisor.Spawn(resumeDispatch, profile, "m1");

            // ── The resumed worker opened its session with session/load, not session/new ─
            //
            // This is the same claim the old argv assertion made, at the place it now lives.
            // A resume that silently became a cold start is the §11 failure this whole area
            // exists to prevent, and under ACP the only difference between the two is a
            // method name on a connection nobody else sees — so the harness records it.
            Assert.True(
                await WaitUntilAsync(
                    () => Task.FromResult(
                        File.Exists(sessionRecord)
                        && File.ReadAllText(sessionRecord).Contains("\"method\":\"session/load\"", StringComparison.Ordinal)),
                    TimeSpan.FromSeconds(15)),
                "the resumed harness never recorded how its session opened");
            var opened = await File.ReadAllTextAsync(sessionRecord, ct);
            Assert.Contains("\"method\":\"session/load\"", opened, StringComparison.Ordinal);
            Assert.Contains(HarnessProgram.EmitStreamSessionId, opened, StringComparison.Ordinal);

            // §13: the answer reached the worker over the authenticated MCP read, and NOT
            // through argv — argv is readable by any local process via ps and
            // /proc/<pid>/cmdline, the same reason enrollment tokens never ride it. Under
            // ACP this is stronger than it was: the argv is now just an entry point, so
            // there is no per-dispatch content on it to leak in the first place.
            var argvPath = Path.Combine(workRoot, sessionId.ToString(), "argv");
            var wholeArgv = File.Exists(argvPath)
                ? string.Join(' ', await File.ReadAllLinesAsync(argvPath, ct))
                : "";
            Assert.DoesNotContain(Answer, wholeArgv, StringComparison.Ordinal);
            Assert.DoesNotContain(Question, wholeArgv, StringComparison.Ordinal);
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

    private async Task<string?> HarnessSessionRefAsync(TimeProvider clock, SessionId id)
    {
        await using var db = pg.NewContext();
        return await db.Sessions.AsNoTracking()
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

    /// <summary>A profile whose cold spawn is <c>emit-stream</c> under
    /// EventsSource.Terminal (so the reader captures + emits the session id) and
    /// whose resume argv re-runs the harness in <c>echo-argv</c> mode with the
    /// resume placeholders the supervisor substitutes.</summary>
    /// <summary>
    /// One profile, both legs. §11 resume needs no second argv now: the ref rides the
    /// dispatch into <c>session/load</c> on the connection the spawn opens, so what used to
    /// be a cold argv plus a resume argv is a single entry point.
    /// </summary>
    private static ProfileConfig AcpProfile() =>
        new(
            "default",
            [HarnessPath(), "--acp"],
            new StopConfig(WindDown: TimeSpan.FromSeconds(30)),
            new TelemetryConfig(Otel: false, Endpoint: null),
            new LogsConfig(),
            MaxConcurrent: null,
            Prompt: "Do the task.",
            FollowUp: "There is new input on your assignment. Read it, then continue.");

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
            new SystemLoad(0, 0, 0), RunningSessions: 0, profiles, DateTimeOffset.UtcNow);
}
