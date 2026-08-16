using Docket.Contracts;
using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Mcp.Auth;
using Docket.Mcp.Tools;
using Docket.Runner;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using HarnessProgram = Docket.Runner.TestHarness.Program;

namespace Docket.Mcp.Tests;

/// <summary>
/// §6/§11 continuation targeting through the surfaces a Lead actually touches: the
/// <c>create_task(continues:)</c> tool orchestration (resolving the continued task's
/// row + the live machine, defaulting the profile, rejecting cross-Team and
/// undeclared-profile requests) and the spawn seam — a continuation dispatch that
/// resumes the inherited session id on its preferred machine, observed as
/// <c>session/load</c> with the inherited id, driven by the real
/// <see cref="ProcessSupervisor"/>,
/// exactly the §11 resume machinery a parked task reuses.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ContinuationEndToEndTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly TeamId Team = TeamId.New();
    private readonly FakeTimeProvider _clock = new();

    private static IHttpContextAccessor AccessorFor(TeamId team) =>
        new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = DocketClaims.ToClaimsPrincipal(new Principal.Lead(team)) },
        };

    private LeadTools LeadFor(TeamId team, RunnerConnectionRegistry registry) =>
        RelayGrantTestKit.LeadToolsFor(pg.NewContext(), _clock, registry, AccessorFor(team));

    /// <summary>Seeds a continued task (Team, profile, harness session ref) and, unless
    /// <paramref name="track"/> is false, makes the registry report it running on
    /// <paramref name="machine"/> with <paramref name="machineProfiles"/>.</summary>
    private async Task<TaskId> SeedContinued(
        RunnerConnectionRegistry registry, TeamId team, string machine, string? profile, string? sessionRef,
        bool track = true, params string[] machineProfiles)
    {
        var profiles = machineProfiles.Length == 0 ? new[] { "default" } : machineProfiles;
        TaskId id;
        await using (var db = pg.NewContext())
        {
            var store = new TaskStore(db, _clock);
            var created = (StoreResult.Applied)await store.CreateAsync(new CreateTask(
                new LeadClaim(team), team, "criteria", CompletionMode.Lead, profile));
            id = created.Task.Id;
            if (sessionRef is not null)
                await store.StampHarnessSessionRefAsync(id, sessionRef);
        }
        if (track)
        {
            registry.Register(machine, new HashSet<string>(profiles, StringComparer.Ordinal), (_, _) => Task.CompletedTask);
            registry.ApplyHeartbeat(machine, new MachineHeartbeat(
                machine, Ready: true, UnderBackPressure: false, new SystemLoad(0, 0, 0),
                RunningTasks: 0, profiles, DateTimeOffset.UtcNow));
            registry.TrackDispatch(machine, id);
        }
        return id;
    }

    private static HashSet<string> Profiles(params string[] names) =>
        new(names.Length == 0 ? ["default"] : names, StringComparer.Ordinal);

    /// <summary>
    /// Seeds a continued task that actually <em>ran</em> on <paramref name="machine"/> and then
    /// finished: dispatched (so a worker-instance row durably records the machine, §12), session
    /// ref stamped, then reported and accepted — which revokes that instance. Nothing tracks it
    /// afterwards and it never parked, which is the ordinary shape of a predecessor a Lead wants
    /// to continue.
    /// </summary>
    private async Task<TaskId> SeedRanAndFinished(TeamId team, string machine, string sessionRef)
    {
        TaskId id;
        WorkerInstanceId instance;
        // Two contexts on purpose: StampHarnessSessionRefAsync is an ExecuteUpdate, so it moves
        // the row's xmin without the tracked entity knowing, and a transition applied afterwards
        // on the same context loses the optimistic-concurrency check it never saw coming.
        await using (var db = pg.NewContext())
        {
            var store = new TaskStore(db, _clock);
            var created = (StoreResult.Applied)await store.CreateAsync(new CreateTask(
                new LeadClaim(team), team, "criteria", CompletionMode.Lead, Profile: null));
            id = created.Task.Id;

            var dispatched = Assert.IsType<StoreResult.Applied>(await store.DispatchNextAsync(
                new MachineSnapshot(machine, Ready: true, UnderBackPressure: false, Profiles()),
                WorkerInstanceId.New()));
            Assert.Equal(id, dispatched.Task.Id);
            instance = dispatched.Task.CurrentInstance!.Value;

            await store.StampHarnessSessionRefAsync(id, sessionRef);
        }
        await using (var db = pg.NewContext())
        {
            var store = new TaskStore(db, _clock);
            Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(
                id, new ReportResult(new WorkerCaller(team, id, instance), "ref")));
            Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(id, new VerdictAccept(new LeadClaim(team))));
        }
        return id;
    }

    [SkippableFact]
    public async Task Create_task_continues_seeds_the_lineage_machine_and_inherited_session()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var registry = new RunnerConnectionRegistry(_clock);
        var continued = await SeedContinued(registry, Team, "m1", profile: null, sessionRef: "sess-1");

        var newIdText = await LeadFor(Team, registry).CreateTask(
            "resume the work", "ship it", "lead", profile: null, workspace: null, CancellationToken.None,
            continues: continued.ToString());

        var newId = Guid.Parse(newIdText);
        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == newId);
        Assert.Equal(continued.Value, row.ContinuesTaskId);
        Assert.Equal("m1", row.PreferredMachine);
        Assert.Equal(MachineGonePolicy.Degrade, row.OnMachineGone); // default policy
        Assert.Equal("sess-1", row.HarnessSessionRef);
    }

    [SkippableFact]
    public async Task Create_task_continues_a_task_in_another_team_is_rejected()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var registry = new RunnerConnectionRegistry(_clock);
        var otherTeam = TeamId.New();
        var foreign = await SeedContinued(registry, otherTeam, "m1", profile: null, sessionRef: "sess-1");

        var ex = await Assert.ThrowsAsync<McpException>(() => LeadFor(Team, registry).CreateTask(
            "resume", "ship it", "lead", null, null, CancellationToken.None, continues: foreign.ToString()));
        Assert.Contains("another Team", ex.Message);
    }

    [SkippableFact]
    public async Task Create_task_continues_with_a_profile_the_preferred_machine_lacks_is_rejected()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var registry = new RunnerConnectionRegistry(_clock);
        // The continued task's machine declares only 'default'; an explicit 'gpu'
        // could never dispatch there, so the tool's resolved facts make the engine
        // refuse at creation.
        var continued = await SeedContinued(
            registry, Team, "m1", profile: null, sessionRef: "sess-1", track: true, machineProfiles: "default");

        var ex = await Assert.ThrowsAsync<McpException>(() => LeadFor(Team, registry).CreateTask(
            "resume", "ship it", "lead", profile: "gpu", workspace: null, CancellationToken.None,
            continues: continued.ToString()));
        Assert.Contains(nameof(Rule.ContinuationProfileDeclaredByPreferredMachine), ex.Message);
    }

    [SkippableFact]
    public async Task Create_task_continues_a_finished_task_whose_machine_is_gone_and_degrade_decides_at_dispatch()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        // THE ordinary continuation, and it used to be refused outright: the predecessor
        // finished, so its process exited and the registry tracks it nowhere, and it never
        // parked — neither of the two live sources can name its machine. The durable
        // worker-instance row can (§12), including after completion revoked that instance.
        var registry = new RunnerConnectionRegistry(_clock); // empty: m1 is gone
        var continued = await SeedRanAndFinished(Team, "m1", "sess-1");

        var newIdText = await LeadFor(Team, registry).CreateTask(
            "carry on", "ship it", "lead", null, null, CancellationToken.None,
            continues: continued.ToString());

        var newId = new TaskId(Guid.Parse(newIdText));
        await using var db = pg.NewContext();
        var row = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == newId.Value);
        Assert.Equal(continued.Value, row.ContinuesTaskId);
        Assert.Equal("m1", row.PreferredMachine); // seeded from the instance row
        Assert.Equal(MachineGonePolicy.Degrade, row.OnMachineGone);
        Assert.Equal("sess-1", row.HarnessSessionRef);
        Assert.Equal(continued.Value, row.WorkDirTaskId); // §11: the directory follows regardless

        // And the machine-gone question is answered where §6/§11 puts it — at dispatch, by the
        // policy the Lead chose. m2 claims it, cold-starts, and the dropped conversation is
        // recorded rather than silently lost. Refusing at creation pre-empted all of this.
        var store = new TaskStore(db, _clock);
        var applied = Assert.IsType<StoreResult.Applied>(await store.DispatchNextAsync(
            new MachineSnapshot("m2", Ready: true, UnderBackPressure: false, Profiles()),
            WorkerInstanceId.New(), CancellationToken.None, connectedMachines: ["m2"]));
        Assert.Equal(newId, applied.Task.Id);
        Assert.Null(applied.HarnessSessionRef);          // cold start: transcript abandoned
        Assert.Equal(continued, applied.WorkDirTask);    // directory still inherited
        Assert.True(await db.TaskEvents.AnyAsync(e =>
            e.TaskId == newId.Value && e.Kind == TaskEventRow.ContinuationMemoryLostKind));
    }

    [SkippableFact]
    public async Task Create_task_continues_a_task_that_never_ran_is_refused()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        // The one case still refused, and it is not the machine-gone case: this task has
        // never been dispatched at all, so there is no transcript to resume and no working
        // directory to carry on in. Nothing a policy could decide later.
        var registry = new RunnerConnectionRegistry(_clock);
        var continued = await SeedContinued(registry, Team, "m1", profile: null, sessionRef: null, track: false);

        var ex = await Assert.ThrowsAsync<McpException>(() => LeadFor(Team, registry).CreateTask(
            "resume", "ship it", "lead", null, null, CancellationToken.None, continues: continued.ToString()));
        Assert.Contains("never been dispatched", ex.Message);
    }

    [SkippableFact]
    public async Task On_machine_gone_without_continues_is_refused()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var registry = new RunnerConnectionRegistry(_clock);

        var ex = await Assert.ThrowsAsync<McpException>(() => LeadFor(Team, registry).CreateTask(
            "do the thing", "ship it", "lead", null, null, CancellationToken.None, onMachineGone: "pin"));
        Assert.Contains("continues", ex.Message);
    }

    [SkippableFact]
    public async Task Continuation_dispatch_resumes_the_inherited_session_at_the_spawn_seam()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var clock = new FakeTimeProvider();
        var workRoot = NewWorkRoot();
        var ring = new OutboundEventRing(capacity: 256);
        var supervisor = new ProcessSupervisor(
            new MachineConfig(workRoot, TimeSpan.FromSeconds(15), BackPressureThresholds.Default), ring, clock);
        var profile = AcpProfile();
        const string inherited = "continued-session-xyz";

        try
        {
            var team = TeamId.New();
            var snapshot = new MachineSnapshot("m1", Ready: true, UnderBackPressure: false, Set("default"));

            TaskId taskId;
            string? resumeRef;
            await using (var db = pg.NewContext())
            {
                var store = new TaskStore(db, clock);
                // A continuation seeded exactly as the tool would, preferring m1 with an
                // inherited session ref. No real continued row needed — the seeding is
                // what drives resume (the engine only validated Team + profile).
                var created = (StoreResult.Applied)await store.CreateAsync(new CreateTask(
                    new LeadClaim(team), team, "criteria", CompletionMode.Lead, null,
                    Continues: new Continuation(TaskId.New(), team, "m1", inherited, MachineGonePolicy.Degrade, null)));
                taskId = created.Task.Id;

                // First dispatch prefers m1 and carries the inherited ref back.
                var applied = (StoreResult.Applied)await store.DispatchNextAsync(
                    snapshot, WorkerInstanceId.New(), ct, ["m1"]);
                Assert.Equal(taskId, applied.Task.Id);
                resumeRef = applied.HarnessSessionRef;
                Assert.Equal(inherited, resumeRef);
            }

            // The DispatchCommand DispatchService would build, spawned through the real
            // supervisor: resuming (ref present + profile declares resume.args) rebuilds
            // session/load carrying the inherited id, on the connection the spawn opens.
            var dispatch = new DispatchCommand(
                taskId, "default", WorkerToken: "worker-1",
                McpConfigJson: """{"mcpServers":{}}""", ResumeSessionRef: resumeRef);
            supervisor.Spawn(dispatch, profile, "m1");

            var sessionPath = Path.Combine(workRoot, taskId.ToString(), "acp_session.json");
            Assert.True(
                await WaitUntilAsync(
                    () => Task.FromResult(
                        File.Exists(sessionPath)
                        && File.ReadAllText(sessionPath).Contains("\"method\":\"session/load\"", StringComparison.Ordinal)),
                    TimeSpan.FromSeconds(15)),
                "the continuation's harness never recorded how its session opened");
            var opened = await File.ReadAllTextAsync(sessionPath, ct);
            Assert.Contains("\"method\":\"session/load\"", opened, StringComparison.Ordinal);
            Assert.Contains(inherited, opened, StringComparison.Ordinal);
        }
        finally
        {
            try { supervisor.KillAll(); } catch { /* best effort */ }
            ring.Complete();
            TryDeleteRoot(workRoot);
        }
    }

    // ── Helpers (mirrors ResumeTranscriptEndToEndTests) ───────────────────────────

    private static string HarnessPath()
    {
        var dll = typeof(HarnessProgram).Assembly.Location;
        var dir = Path.GetDirectoryName(dll)!;
        var stem = Path.GetFileNameWithoutExtension(dll);
        var apphost = Path.Combine(dir, OperatingSystem.IsWindows() ? stem + ".exe" : stem);
        return File.Exists(apphost) ? apphost : dll;
    }

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
        var dir = Path.Combine(Path.GetTempPath(), "docket-continuation-crown", Guid.NewGuid().ToString("N"));
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
}
