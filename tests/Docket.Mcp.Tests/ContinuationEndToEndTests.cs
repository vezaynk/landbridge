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
/// <c>--resume &lt;id&gt;</c> in the real <see cref="ProcessSupervisor"/> argv,
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
                new LeadClaim(team), team, "criteria", CompletionMode.Lead, profile, TeamBudgetRemains: true));
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
    public async Task Create_task_continues_a_task_whose_machine_is_gone_is_refused()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var registry = new RunnerConnectionRegistry(_clock);
        // The continued task exists but is not tracked anywhere and was never parked,
        // so the plane can't locate its machine-local session — there is nothing to
        // resume, and the tool says so rather than silently cold-starting.
        var continued = await SeedContinued(registry, Team, "m1", profile: null, sessionRef: "sess-1", track: false);

        var ex = await Assert.ThrowsAsync<McpException>(() => LeadFor(Team, registry).CreateTask(
            "resume", "ship it", "lead", null, null, CancellationToken.None, continues: continued.ToString()));
        Assert.Contains("no longer", ex.Message);
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
        var profile = ResumeProfile();
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
                    new LeadClaim(team), team, "criteria", CompletionMode.Lead, null, TeamBudgetRemains: true,
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
            // the argv from resume.args with --resume <inherited id>.
            var dispatch = new DispatchCommand(
                taskId, "default", WorkerToken: "worker-1",
                McpConfigJson: """{"mcpServers":{}}""", ResumeSessionRef: resumeRef);
            supervisor.Spawn(dispatch, profile, "m1");

            var argvPath = Path.Combine(workRoot, taskId.ToString(), "argv");
            Assert.True(await WaitUntilAsync(() => Task.FromResult(File.Exists(argvPath)), TimeSpan.FromSeconds(15)),
                "the resumed harness never recorded its argv");
            var argv = await File.ReadAllLinesAsync(argvPath, ct);
            var resumeIdx = Array.IndexOf(argv, "--resume");
            Assert.True(resumeIdx >= 0, $"resumed argv carried no --resume: {string.Join(' ', argv)}");
            Assert.Equal(inherited, argv[resumeIdx + 1]);
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

    private static ProfileConfig ResumeProfile() =>
        new(
            "default",
            [HarnessPath(), "emit-stream"],
            new StopConfig(StopMode.Signal, MessageTemplate: null, WindDown: TimeSpan.FromSeconds(30)),
            new ResumeConfig([HarnessPath(), "echo-argv", "--resume", "{session_id}", "--mcp-config", "{mcp_config}"]),
            new EventsConfig(EventsSource.Terminal, new Dictionary<string, string>()),
            new TelemetryConfig(Otel: false, Endpoint: null),
            new LogsConfig(),
            MaxConcurrent: null);

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
