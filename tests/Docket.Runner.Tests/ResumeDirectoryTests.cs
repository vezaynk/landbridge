using Docket.Contracts;
using Docket.Core;
using Microsoft.Extensions.Time.Testing;

namespace Docket.Runner.Tests;

/// <summary>
/// §11 resume, directory half: a harness session is directory-local as well as machine-local
/// — Claude Code resumes a session only from the directory that created it. A same-task
/// park-resume reuses that task's own dir, but a <c>continues:</c> continuation runs under a
/// NEW task id, so the plane names the task whose dir holds the session
/// (<see cref="DispatchCommand.ResumeFromTask"/>) and the harness runs there.
///
/// <para>That deliberately puts two task ids in one directory, which §7 accepts for
/// continuations — the workspace <em>is</em> the work, and a continuation inheriting it is the
/// point. These facts pin the consequences: the dispatched task keeps its own identity for
/// everything the runner keys on (supervision, reaping, the injected credential), so sharing a
/// dir is not sharing a task.</para>
/// </summary>
public sealed class ResumeDirectoryTests : IDisposable
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
    /// The whole point: a resumed continuation runs in the named task's dir, and the
    /// substituted <c>{mcp_config}</c> lands there too, so <c>--resume</c> is issued from the
    /// directory that actually created the session.
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
                ResumeSessionRef: "sess-abc", ResumeFromTask: predecessor),
            TestKit.ResumeProfile(), "m");

        // The harness ran in the PREDECESSOR's dir — it wrote its argv marker there…
        var argv = await ArgvIn(predecessor);
        var resumeIdx = Array.IndexOf(argv, "--resume");
        Assert.True(resumeIdx >= 0, "resume argv did not carry --resume");
        Assert.Equal("sess-abc", argv[resumeIdx + 1]);

        // …and its config was written there under a task-scoped name, because the plain one
        // belongs to the task that owns the dir and its worker may still be running.
        var mcpIdx = Array.IndexOf(argv, "--mcp-config");
        Assert.True(mcpIdx >= 0, "resume argv did not carry --mcp-config");
        Assert.Equal(
            Path.Combine(_workRoot, predecessor.ToString(), $"mcp-{continuation}.json"),
            argv[mcpIdx + 1]);
        Assert.True(File.Exists(argv[mcpIdx + 1]));
        // Nothing was written into the continuation's own dir, and nothing clobbered the
        // predecessor's own config name.
        Assert.False(File.Exists(Path.Combine(_workRoot, predecessor.ToString(), "mcp.json")));
        Assert.False(Directory.Exists(Path.Combine(_workRoot, continuation.ToString())));

        supervisor.Kill(continuation);
    }

    /// <summary>
    /// A cold start ignores the field entirely. It is honoured only while actually resuming,
    /// so a profile with no <c>resume.args</c> — or a dispatch whose resume ref was suppressed
    /// (a degrade cold-start) — always gets its own dir, whatever else the dispatch carries.
    /// </summary>
    [Fact]
    public async Task A_cold_start_ignores_the_named_directory_and_uses_its_own()
    {
        var predecessor = TaskId.New();
        var task = TaskId.New();
        var supervisor = NewSupervisor();

        // No resume ref → cold start, even though a directory is named.
        supervisor.Spawn(
            new DispatchCommand(
                task, "default", McpConfigJson: """{"mcpServers":{}}""",
                ResumeFromTask: predecessor),
            TestKit.ResumeProfile(), "m");

        var argv = await ArgvIn(task);
        Assert.DoesNotContain("--resume", argv);
        Assert.False(Directory.Exists(Path.Combine(_workRoot, predecessor.ToString())));
        Assert.True(File.Exists(Path.Combine(_workRoot, task.ToString(), "mcp.json")));

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
            TestKit.ResumeProfile(), "m");

        var argv = await ArgvIn(task);
        Assert.Contains("--resume", argv);
        var mcpIdx = Array.IndexOf(argv, "--mcp-config");
        Assert.Equal(Path.Combine(_workRoot, task.ToString(), "mcp.json"), argv[mcpIdx + 1]);

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
                continuation, "default", ResumeSessionRef: "sess-abc", ResumeFromTask: predecessor),
            TestKit.ResumeProfile("sleeper"), "m");
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
