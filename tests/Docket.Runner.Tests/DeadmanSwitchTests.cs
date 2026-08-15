using Docket.Core;
using Microsoft.Extensions.Time.Testing;

namespace Docket.Runner.Tests;

/// <summary>
/// §10's dead-man's switch, against real processes: docketd holds the write end of every
/// worker's stdin for that worker's whole life, so EOF means docketd is gone.
///
/// <para>Under ACP that same pipe is the JSON-RPC request channel, and the protocol's own
/// shutdown rule — the client closes stdin to terminate the agent — is the identical
/// mechanism. So this is one convention serving two purposes rather than two conventions
/// competing, which is why the per-profile <c>stdin: closed</c> opt-out that used to live
/// here is gone: it existed for harnesses that blocked reading a held-open pipe while
/// resolving an <em>argv prompt</em>, and stdin no longer carries one.</para>
/// </summary>
public sealed class DeadmanSwitchTests : IDisposable
{
    private readonly string _workRoot = TestKit.NewWorkRoot();
    private readonly FakeTimeProvider _clock = new();
    private readonly OutboundEventRing _ring = new(capacity: 256);
    private ProcessSupervisor? _supervisor;

    private ProcessSupervisor Supervisor() =>
        _supervisor ??= new ProcessSupervisor(TestKit.Machine(_workRoot), _ring, _clock);

    private async Task<List<RunnerEvent>> DrainedEventsAsync()
    {
        _ring.Complete();
        var events = new List<RunnerEvent>();
        await foreach (var item in _ring.ReadAllAsync(CancellationToken.None))
            events.Add(item.Event);
        return events;
    }


    /// <summary>
    /// The default is unchanged, and this is the fact that says so: with no <c>stdin</c>
    /// declared the pipe is held open, the worker sits in its stdin read, and it neither
    /// sees EOF nor exits. Without this the fix above could have been "docketd now always
    /// closes stdin", which would silently remove the §10 dead-man's switch from every
    /// existing profile in the fleet.
    /// </summary>
    [Fact]
    public async Task The_deadman_default_still_holds_the_pipe_open_and_the_worker_keeps_running()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();

        supervisor.Spawn(TestKit.Dispatch(task), TestKit.Profile("run"), "machine-42");
        Assert.True(supervisor.TryGet(task, out var supervised));

        // Wait until it is demonstrably up and reading, so "no deadman marker" below means
        // "it did not see EOF" rather than "it had not started yet".
        var started = Path.Combine(_workRoot, task.ToString(), "started");
        Assert.True(
            await TestKit.WaitUntilAsync(() => File.Exists(started), TimeSpan.FromSeconds(20)),
            "harness never wrote its started marker");

        var deadman = Path.Combine(_workRoot, task.ToString(), "deadman");
        Assert.False(
            await TestKit.WaitUntilAsync(() => File.Exists(deadman), TimeSpan.FromSeconds(3)),
            "a default-profile worker saw EOF on stdin — the dead-man pipe is no longer being "
            + "held open, so docketd's death would no longer be visible to any worker");
        Assert.True(supervised!.ProcessAlive);

        Assert.True(supervisor.Kill(task));
    }

    /// <summary>
    /// The dead-man's switch still works for a <c>deadman</c> profile spawned through the
    /// supervisor — closing the write end is byte-identical to what the OS does when docketd
    /// dies, so this is the switch itself, reached through the spawn path the policy now
    /// branches in. Pinned here because the two policies share one line of code: a
    /// regression that closed the pipe too late, or on the wrong side of the branch, would
    /// pass the two facts above and break this.
    /// </summary>
    [Fact]
    public async Task A_deadman_worker_still_trips_its_switch_when_the_pipe_closes()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();

        supervisor.Spawn(TestKit.Dispatch(task), TestKit.Profile("run"), "m");
        Assert.True(supervisor.TryGet(task, out var supervised));

        var started = Path.Combine(_workRoot, task.ToString(), "started");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(started), TimeSpan.FromSeconds(20)));

        // Stand in for docketd's death.
        supervised!.Process.StandardInput.Close();

        var deadman = Path.Combine(_workRoot, task.ToString(), "deadman");
        Assert.True(
            await TestKit.WaitUntilAsync(() => File.Exists(deadman), TimeSpan.FromSeconds(20)),
            "a deadman-profile worker did not trip its switch on pipe close");
        Assert.True(await TestKit.WaitUntilAsync(() => !supervised.ProcessAlive, TimeSpan.FromSeconds(20)));
        Assert.Equal(TestHarness.Program.DeadManExitCode, supervised.Process.ExitCode);
    }



    public void Dispose()
    {
        _supervisor?.KillAll();
        TestKit.TryDeleteRoot(_workRoot);
    }
}
