using Docket.Core;
using Microsoft.Extensions.Time.Testing;
using HarnessProgram = Docket.Runner.TestHarness.Program;

namespace Docket.Runner.Tests;

/// <summary>
/// §10 event relay end-to-end through <see cref="ProcessSupervisor"/>: a real
/// spawned harness in <c>emit-stream</c> mode writes a claude-style stream-json
/// transcript to stdout, the supervisor redirects and drains it under
/// <see cref="EventsSource.Terminal"/>, and the drain maps it to ToolCallEvents
/// and per-task liveness. Also pins the redirect behaviour: only Terminal
/// redirects stdout; every other source leaves it alone.
/// </summary>
public sealed class TerminalEventsSupervisorTests : IDisposable
{
    private readonly string _workRoot = TestKit.NewWorkRoot();
    private readonly FakeTimeProvider _clock = new();
    private readonly OutboundEventRing _ring = new(capacity: 256);
    private ProcessSupervisor? _supervisor;

    private ProcessSupervisor Supervisor() =>
        _supervisor ??= new ProcessSupervisor(TestKit.Machine(_workRoot), _ring, _clock);

    private static ProfileConfig TerminalProfile() =>
        TestKit.Profile("emit-stream", events: EventsSource.Terminal);

    [Fact]
    public async Task Terminal_source_drains_stdout_into_tool_call_events_and_captures_session_id()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();

        // A background drain — the ring is single-reader, so this loop is the one
        // consumer while the harness stays alive on stdin.
        var drained = new List<RunnerEvent>();
        using var drainCts = new CancellationTokenSource();
        var drainLoop = Task.Run(async () =>
        {
            await foreach (var item in _ring.ReadAllAsync(drainCts.Token))
                lock (drained) drained.Add(item.Event);
        });

        supervisor.Spawn(TestKit.Dispatch(task), TerminalProfile(), "machine-term");

        // Make the spawn-time liveness stamp stale up front, so a later "live" can
        // only come from the reader's own RecordActivity on the streamed lines.
        _clock.Advance(TimeSpan.FromSeconds(60));

        // The transcript's two tool_use blocks map to ToolCallEvents in order.
        Assert.True(
            await TestKit.WaitUntilAsync(() => ToolNames(drained).Count >= 2, TimeSpan.FromSeconds(20)),
            "terminal reader never drained the expected tool-call events");
        Assert.Equal(HarnessProgram.EmitStreamToolNames, ToolNames(drained));

        // Per-task liveness reflects the streamed activity: the reader stamped
        // RecordActivity at the post-advance clock, so the task reads live again.
        Assert.True(supervisor.IsTaskLive(task, TimeSpan.FromSeconds(10)));

        // system/init session id was captured onto the task (reserved for §11 resume).
        Assert.True(
            await TestKit.WaitUntilAsync(
                () => supervisor.TryGet(task, out var s) && s.SessionId == HarnessProgram.EmitStreamSessionId,
                TimeSpan.FromSeconds(10)),
            "terminal reader never captured the harness session id");

        supervisor.Kill(task);
        _ring.Complete();
        await drainLoop;
    }

    [Fact]
    public async Task Terminal_source_redirects_stdout_other_sources_do_not()
    {
        var supervisor = Supervisor();

        var terminalTask = TaskId.New();
        supervisor.Spawn(TestKit.Dispatch(terminalTask), TerminalProfile(), "m");
        Assert.True(supervisor.TryGet(terminalTask, out var terminal));
        Assert.True(terminal.Process.StartInfo.RedirectStandardOutput);
        Assert.NotNull(terminal.EventReaderTask);

        // A non-terminal (default None) profile spawns exactly as before: stdout
        // unredirected, no drain task.
        var plainTask = TaskId.New();
        supervisor.Spawn(TestKit.Dispatch(plainTask), TestKit.Profile("run"), "m");
        Assert.True(supervisor.TryGet(plainTask, out var plain));
        Assert.False(plain.Process.StartInfo.RedirectStandardOutput);
        Assert.Null(plain.EventReaderTask);

        supervisor.Kill(terminalTask);
        supervisor.Kill(plainTask);
    }

    [Fact]
    public async Task Terminal_source_emits_exactly_one_session_started_event_from_system_init()
    {
        // §11 resume: the supervisor emits ONE SessionStartedEvent carrying the
        // harness session ref the moment the terminal reader captures system/init —
        // so the plane can stamp it on the task row before any park.
        var task = TaskId.New();
        var supervisor = Supervisor();

        var drained = new List<RunnerEvent>();
        using var drainCts = new CancellationTokenSource();
        var drainLoop = Task.Run(async () =>
        {
            await foreach (var item in _ring.ReadAllAsync(drainCts.Token))
                lock (drained) drained.Add(item.Event);
        });

        supervisor.Spawn(TestKit.Dispatch(task), TerminalProfile(), "machine-term");

        Assert.True(
            await TestKit.WaitUntilAsync(() => SessionEvents(drained).Count >= 1, TimeSpan.FromSeconds(20)),
            "supervisor never emitted a session-started event");

        supervisor.Kill(task);
        _ring.Complete();
        await drainLoop;

        // Exactly one, carrying the fixture's known session id and the task id.
        var evt = Assert.Single(SessionEvents(drained));
        Assert.Equal(task, evt.Task);
        Assert.Equal(HarnessProgram.EmitStreamSessionId, evt.SessionRef);
    }

    private static List<string> ToolNames(List<RunnerEvent> drained)
    {
        lock (drained)
            return drained.OfType<ToolCallEvent>().Select(e => e.Tool).ToList();
    }

    private static List<SessionStartedEvent> SessionEvents(List<RunnerEvent> drained)
    {
        lock (drained)
            return drained.OfType<SessionStartedEvent>().ToList();
    }

    public void Dispose()
    {
        try { _supervisor?.KillAll(); } catch { /* best effort */ }
        TestKit.TryDeleteRoot(_workRoot);
    }
}
