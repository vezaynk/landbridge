using Docket.Contracts;
using Docket.Core;
using Microsoft.Extensions.Time.Testing;

namespace Docket.Runner.Tests;

/// <summary>
/// Which directory a task's harness runs in (§7, §11). Ordinarily its own; for a
/// <c>continues:</c> continuation, its predecessor's — named by the plane as
/// <see cref="DispatchCommand.WorkDirTask"/>, because a continuation runs under a NEW task id
/// and the runner's own directory for it would be empty.
///
/// <para>Inheritance is a property of <b>continuation</b>, not of resume, and one of these
/// facts exists to pin exactly that: a cold start with no session ref still runs there,
/// because the worktree and artifacts the predecessor left are what the successor needs — the
/// workspace is the work. Transcript resume additionally requires it (a harness session is
/// directory-local, so Claude Code resumes only from the creating directory) but does not
/// define it.</para>
///
/// <para>That deliberately puts two task ids in one directory, which §7 accepts for
/// continuations. These facts pin the consequences: the dispatched task keeps its own identity
/// for everything the runner keys on (supervision, reaping, the injected credential), so
/// sharing a dir is not sharing a task.</para>
/// </summary>
public sealed class WorkDirectoryTests : IDisposable
{
    private readonly string _workRoot = TestKit.NewWorkRoot();
    private readonly FakeTimeProvider _clock = new();
    private readonly OutboundEventRing _ring = new(capacity: 256);

    private ProcessSupervisor NewSupervisor(StrayReaper? reaper = null) =>
        new(TestKit.Machine(_workRoot), _ring, _clock, reaper);

    private async Task<string[]> ArgvIn(TaskId dirTask)
    {
        var path = Path.Combine(_workRoot, dirTask.ToString(), "argv");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(path), TimeSpan.FromSeconds(15)),
            $"harness never recorded its argv in {dirTask}'s dir");
        return await File.ReadAllLinesAsync(path);
    }

    /// <summary>
    /// The whole point: a resumed continuation runs in the named task's dir, so the session
    /// it loads is loaded from the directory that created it (§11 sessions are
    /// directory-scoped). The ref itself now travels to <c>session/load</c> rather than into
    /// a respawned argv, so what this fact still owns is the working directory.
    /// </summary>
    [Fact]
    public async Task A_resume_that_names_another_tasks_directory_runs_there()
    {
        var predecessor = TaskId.New();
        var continuation = TaskId.New();
        var supervisor = NewSupervisor();

        supervisor.Spawn(
            new DispatchCommand(
                continuation, "default", McpConfigJson: """{"mcpServers":{}}""",
                ResumeSessionRef: "sess-abc", WorkDirTask: predecessor),
            TestKit.Profile("echo-argv"), "m");

        // The harness ran in the PREDECESSOR's dir — it wrote its argv marker there. The
        // resume ref itself is no longer visible here: it rides the dispatch into
        // session/load rather than a respawned argv (ResumeTranscriptEndToEndTests asserts
        // that end). What this fact is about is the DIRECTORY, and that is unchanged.
        var argv = await ArgvIn(predecessor);
        Assert.NotEmpty(argv);

        // …and nothing was written into the continuation's own dir. The borrowed-directory
        // hazard this fact guards is that a continuation running in its predecessor's dir
        // must not clobber what the predecessor owns — still true, and now with less to
        // clobber, since an ACP profile names no {mcp_config} and so no config file is
        // written at all (#112 G11 gates that write on the argv actually asking for it).
        Assert.False(File.Exists(Path.Combine(_workRoot, predecessor.ToString(), "mcp.json")));
        Assert.False(Directory.Exists(Path.Combine(_workRoot, continuation.ToString())));

        supervisor.Kill(continuation);
    }

    /// <summary>
    /// A <b>cold start</b> honours the named directory too, and this is the fact that says
    /// directory inheritance belongs to continuation rather than to resume: no session ref,
    /// no <c>--resume</c>, and the harness still runs where its predecessor worked — because
    /// the worktree and artifacts it needs are there, whether or not the conversation
    /// survived. A degrade cold-start is exactly this shape.
    /// </summary>
    [Fact]
    public async Task A_cold_start_still_runs_in_the_named_directory()
    {
        var predecessor = TaskId.New();
        var task = TaskId.New();
        var supervisor = NewSupervisor();

        // Something the predecessor left behind — the reason to be in its directory at all.
        var predecessorDir = Directory.CreateDirectory(
            Path.Combine(_workRoot, predecessor.ToString())).FullName;
        await File.WriteAllTextAsync(Path.Combine(predecessorDir, "artifact.txt"), "left behind");

        // No resume ref → cold start, and a directory is named.
        supervisor.Spawn(
            new DispatchCommand(
                task, "default", McpConfigJson: """{"mcpServers":{}}""",
                WorkDirTask: predecessor),
            TestKit.Profile("echo-argv"), "m");

        var argv = await ArgvIn(predecessor);          // it ran in the predecessor's dir…
        Assert.DoesNotContain("--resume", argv);        // …on the cold argv, with no resume
        // The predecessor's work is what it can see from there.
        Assert.True(File.Exists(Path.Combine(predecessorDir, "artifact.txt")));
        // Its own dir was never used. Cold-start spawn argv does not name {mcp_config},
        // so no bearer file is written into the borrowed dir (#112 G11).
        Assert.False(Directory.Exists(Path.Combine(_workRoot, task.ToString())));
        Assert.False(File.Exists(Path.Combine(predecessorDir, $"mcp-{task}.json")));
        Assert.False(File.Exists(Path.Combine(predecessorDir, "mcp.json")));

        supervisor.Kill(task);
    }

    /// <summary>
    /// A same-task park-resume is unchanged: no directory named, own dir, and the documented
    /// <c>mcp.json</c> name. The regression guard on the common path.
    /// </summary>
    [Fact]
    public async Task A_same_task_resume_still_uses_its_own_directory_and_config_name()
    {
        var task = TaskId.New();
        var supervisor = NewSupervisor();

        supervisor.Spawn(
            new DispatchCommand(
                task, "default", McpConfigJson: """{"mcpServers":{}}""", ResumeSessionRef: "sess-abc"),
            TestKit.Profile("echo-argv"), "m");

        // Its own dir, and the plain config name is free: a task resuming itself borrows
        // nothing, so there is no task-scoped rename and — since an ACP profile names no
        // {mcp_config} — no config file written at all.
        var argv = await ArgvIn(task);
        Assert.NotEmpty(argv);
        Assert.False(File.Exists(Path.Combine(_workRoot, task.ToString(), $"mcp-{task}.json")));

        supervisor.Kill(task);
    }

    /// <summary>
    /// Sharing a directory is not sharing a task (#102's supersede guard keys on task id, and
    /// has to keep doing so now that dir and identity are distinct). A continuation dispatched
    /// into a live predecessor's dir must not supersede it, kill it, or reap it — those are
    /// different tasks, and a continuation of a task that has not finished is allowed (§11
    /// forks and chains).
    /// </summary>
    [Fact]
    public async Task A_continuation_sharing_a_directory_does_not_supersede_or_reap_the_task_that_owns_it()
    {
        var predecessor = TaskId.New();
        var continuation = TaskId.New();
        var inventory = new DirSharingInventory();
        var supervisor = NewSupervisor(new StrayReaper(inventory, selfPid: Environment.ProcessId));

        supervisor.Spawn(TestKit.Dispatch(predecessor), TestKit.Profile("sleeper"), "m");
        Assert.True(supervisor.TryGet(predecessor, out var owner));

        supervisor.Spawn(
            new DispatchCommand(
                continuation, "default", ResumeSessionRef: "sess-abc", WorkDirTask: predecessor),
            TestKit.Profile("echo-argv"), "m");
        Assert.True(supervisor.TryGet(continuation, out var guest));

        // Two live workers, two supervised tasks, one directory. Neither was superseded:
        // that is keyed on task id, and these are different tasks.
        Assert.True(owner.ProcessAlive);
        Assert.True(guest.ProcessAlive);
        Assert.Equal(2, supervisor.RunningTotal);
        Assert.Equal(0, supervisor.SupersededExits);
        Assert.Equal(owner.WorkDir, guest.WorkDir);

        // The owner dying reports its OWN exit and reaps only by its own DOCKET_TASK_ID, so
        // the guest sharing its directory is untouched.
        inventory.Processes = [new TaggedProcess(guest.Process.Id, "m", continuation.ToString())];
        Assert.True(supervisor.Kill(predecessor));
        Assert.True(await TestKit.WaitUntilAsync(() => !owner.ProcessAlive, TimeSpan.FromSeconds(15)));

        Assert.True(guest.ProcessAlive);
        Assert.True(TestKit.PidAlive(guest.Process.Id));
        Assert.True(supervisor.TryGet(continuation, out _));

        Assert.True(supervisor.Kill(continuation));
    }

    public void Dispose() => TestKit.TryDeleteRoot(_workRoot);

    /// <summary>An inventory seeded after the pids it reports exist, so a test can stand a
    /// live process in front of the reaper and prove it was left alone.</summary>
    private sealed class DirSharingInventory : IProcessInventory
    {
        public IReadOnlyList<TaggedProcess> Processes { get; set; } = [];

        public IReadOnlyList<TaggedProcess> ListDocketProcesses() => Processes;
    }
}
