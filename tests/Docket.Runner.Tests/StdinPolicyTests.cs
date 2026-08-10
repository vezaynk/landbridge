using Docket.Core;
using Microsoft.Extensions.Time.Testing;

namespace Docket.Runner.Tests;

/// <summary>
/// §10 per-profile <c>stdin</c> policy (#110), against real spawned processes. The
/// dead-man's switch used to be unconditional: docketd redirected every worker's stdin and
/// held the write end for the task's life, so EOF meant "docketd is gone". That is still
/// the default and still what <see cref="DeadMansSwitchTests"/> pins — but it is not
/// universally survivable. <c>codex exec</c> reads stdin to EOF while resolving its prompt
/// even when the prompt came in as argv, and no flag escapes it, so a Codex worker under the
/// held-open pipe blocks forever without ever contacting a model. <c>stdin: closed</c> is
/// the escape.
///
/// <para><b>What makes these facts rather than assertions about a flag:</b> the test harness
/// really reads its stdin, and on EOF it writes the <c>deadman</c> marker and exits with
/// <see cref="TestHarness.Program.DeadManExitCode"/>. So "EOF arrived" is observable from
/// outside the process, and nobody here closes anything — under <c>closed</c> the EOF comes
/// from docketd's own spawn path, which is the entire claim.</para>
/// </summary>
public sealed class StdinPolicyTests : IDisposable
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
    /// The fix itself: a <c>stdin: closed</c> worker sees EOF immediately, with docketd alive
    /// and well and nothing in the test touching the pipe. The harness's <c>run</c> mode
    /// blocks on stdin exactly as <c>codex exec</c> does, so this is the same wait Codex was
    /// stuck in — and it ends here in milliseconds instead of never.
    ///
    /// <para>The exit code is what makes it unambiguous. A harness that merely died would
    /// give some other code; <see cref="TestHarness.Program.DeadManExitCode"/> is written
    /// only on the EOF path, after the <c>deadman</c> marker, so both together say "the
    /// worker read its stdin, reached end of stream, and acted on it".</para>
    /// </summary>
    [Fact]
    public async Task A_closed_stdin_worker_sees_eof_at_once_without_docketd_dying()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();

        supervisor.Spawn(
            TestKit.Dispatch(task), TestKit.Profile("run", stdin: StdinPolicy.Closed), "machine-42");
        // Hold the handle: OnExited drops the task from the running set, and the exit code is
        // the assertion.
        Assert.True(supervisor.TryGet(task, out var supervised));

        var deadman = Path.Combine(_workRoot, task.ToString(), "deadman");
        Assert.True(
            await TestKit.WaitUntilAsync(() => File.Exists(deadman), TimeSpan.FromSeconds(20)),
            "the worker never saw EOF on stdin — docketd is still holding the write end open, "
            + "which is exactly the state that makes a codex exec worker hang forever");
        Assert.True(
            await TestKit.WaitUntilAsync(() => !supervised!.ProcessAlive, TimeSpan.FromSeconds(20)),
            "the worker saw EOF but never exited");
        Assert.Equal(TestHarness.Program.DeadManExitCode, supervised!.Process.ExitCode);

        // And docketd reports it as an ordinary exit — a closed-stdin spawn is not a special
        // lifecycle, just an earlier EOF.
        var events = await DrainedEventsAsync();
        Assert.Contains(events, e => e is StartedEvent s && s.Task == task);
        Assert.Contains(
            events,
            e => e is ExitedEvent x && x.Task == task && x.ExitCode == TestHarness.Program.DeadManExitCode);
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

    /// <summary>
    /// The stop-path interaction, from the supervisor's side. A loaded config can never
    /// produce this pairing — <c>RunnerConfigTests</c> pins that <c>stop.mode: message</c>
    /// with <c>stdin: closed</c> is refused at validation, because a wind-down turn with
    /// nowhere to land is a contradiction rather than a degradation — but the supervisor is
    /// also driven directly, and it must not claim a delivery it cannot make.
    ///
    /// <para>So the ack is <see cref="StopDelivery.DeadlineArmed"/>: nothing was written,
    /// and docketd says nothing was written. That is the #103 honesty rule applied to the
    /// one case where the transport is missing rather than the harness uncooperative.</para>
    /// </summary>
    [Fact]
    public async Task A_stop_on_a_closed_stdin_worker_arms_the_deadline_instead_of_claiming_a_written_turn()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();

        // `stdin-stop` would honour a written turn if one could reach it, so a MessageWritten
        // ack here would be a real false claim rather than a technicality.
        supervisor.Spawn(
            TestKit.Dispatch(task),
            TestKit.Profile("stdin-stop", StopMode.Message, stdin: StdinPolicy.Closed),
            "m");

        var ack = await supervisor.StopAsync(
            task, TimeSpan.FromSeconds(30), StopDisposition.Preserve, "wind down", CancellationToken.None);

        Assert.True(ack.Actioned);
        Assert.Equal(StopDelivery.DeadlineArmed, ack.Delivery);
        Assert.NotEqual(StopDelivery.MessageWritten, ack.Delivery);

        // No turn was written, so the harness cannot have recorded one — the `stopped` marker
        // is only written when it reads a stop line.
        Assert.False(File.Exists(Path.Combine(_workRoot, task.ToString(), "stopped")));

        supervisor.Kill(task);
    }

    /// <summary>
    /// The two spawn-path features that landed together compose: a §11 continuation borrowing
    /// its predecessor's working directory (#117) still gets its EOF when the profile declares
    /// <c>stdin: closed</c> (#110).
    ///
    /// <para>Worth one fact because the two touch the same method from opposite ends — the
    /// directory is resolved before <c>Process.Start</c>, the stdin close happens after it —
    /// and because the combination is not hypothetical: <c>codex exec resume</c> is the one
    /// Codex path that never needed the fix (it takes its prompt from argv without reading
    /// stdin), so a real Codex continuation is exactly a borrowed directory under a
    /// closed-stdin profile.</para>
    ///
    /// <para>The <c>deadman</c> marker landing in the <em>borrowed</em> directory is what makes
    /// this one assertion cover both: the harness only writes it on EOF, and it writes it in
    /// its own working directory.</para>
    /// </summary>
    [Fact]
    public async Task A_continuation_borrowing_another_tasks_directory_still_gets_its_eof()
    {
        var predecessor = TaskId.New();
        var continuation = TaskId.New();
        var supervisor = Supervisor();

        supervisor.Spawn(
            new DispatchCommand(
                continuation, "default", McpConfigJson: """{"mcpServers":{}}""",
                ResumeSessionRef: "sess-abc", WorkDirTask: predecessor),
            TestKit.ResumeProfile(stdin: StdinPolicy.Closed),
            "m");
        Assert.True(supervisor.TryGet(continuation, out var supervised));

        var deadman = Path.Combine(_workRoot, predecessor.ToString(), "deadman");
        Assert.True(
            await TestKit.WaitUntilAsync(() => File.Exists(deadman), TimeSpan.FromSeconds(20)),
            "a closed-stdin continuation never saw EOF in the directory it borrowed — the "
            + "stdin policy and the inherited work dir are not composing");
        Assert.True(
            await TestKit.WaitUntilAsync(() => !supervised!.ProcessAlive, TimeSpan.FromSeconds(20)));
        Assert.Equal(TestHarness.Program.DeadManExitCode, supervised!.Process.ExitCode);

        // The continuation still never got a directory of its own, exactly as #117 requires —
        // closing stdin changed nothing about where the harness ran.
        Assert.False(Directory.Exists(Path.Combine(_workRoot, continuation.ToString())));
    }

    public void Dispose()
    {
        _supervisor?.KillAll();
        TestKit.TryDeleteRoot(_workRoot);
    }
}
