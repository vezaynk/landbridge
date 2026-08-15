using Docket.Core;
using Microsoft.Extensions.Time.Testing;
using HarnessProgram = Docket.Runner.TestHarness.Program;

namespace Docket.Runner.Tests;

/// <summary>
/// ACP session/update end-to-end through <see cref="ProcessSupervisor"/>: a real
/// spawned agent emits <c>tool_call</c> updates, and the handshake stamps a
/// session id.
/// </summary>
public sealed class TerminalEventsSupervisorTests : IDisposable
{
    private readonly string _workRoot = TestKit.NewWorkRoot();
    private readonly FakeTimeProvider _clock = new();
    private readonly OutboundEventRing _ring = new(capacity: 256);
    private ProcessSupervisor? _supervisor;

    private ProcessSupervisor Supervisor() =>
        _supervisor ??= new ProcessSupervisor(TestKit.Machine(_workRoot), _ring, _clock);

    [Fact]
    public async Task Acp_tool_calls_drain_into_events_and_the_handshake_stamps_a_session_id()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();

        var drained = new List<RunnerEvent>();
        using var drainCts = new CancellationTokenSource();
        var drainLoop = Task.Run(async () =>
        {
            await foreach (var item in _ring.ReadAllAsync(drainCts.Token))
                lock (drained) drained.Add(item.Event);
        });

        supervisor.Spawn(TestKit.Dispatch(task), TestKit.Profile("emit-stream"), "machine-term");

        Assert.True(
            await TestKit.WaitUntilAsync(() => ToolNames(drained).Count >= 2, TimeSpan.FromSeconds(20)),
            "ACP session/update never produced the expected tool-call events");
        Assert.Equal(HarnessProgram.EmitStreamToolNames, ToolNames(drained));

        Assert.True(
            await TestKit.WaitUntilAsync(
                () => supervisor.TryGet(task, out var s) && !string.IsNullOrWhiteSpace(s.SessionId),
                TimeSpan.FromSeconds(10)),
            "ACP handshake never stamped a session id");

        supervisor.Kill(task);
        _ring.Complete();
        await drainLoop;
    }

    [Fact]
    public async Task Every_worker_redirects_stdio_for_the_acp_pipe()
    {
        var supervisor = Supervisor();

        var a = TaskId.New();
        supervisor.Spawn(TestKit.Dispatch(a), TestKit.Profile("emit-stream"), "m");
        Assert.True(supervisor.TryGet(a, out var first));
        Assert.True(first.Process.StartInfo.RedirectStandardOutput);
        Assert.True(first.Process.StartInfo.RedirectStandardInput);
        Assert.Null(first.EventReaderTask);

        var b = TaskId.New();
        supervisor.Spawn(TestKit.Dispatch(b), TestKit.Profile("run"), "m");
        Assert.True(supervisor.TryGet(b, out var second));
        Assert.True(second.Process.StartInfo.RedirectStandardOutput);
        Assert.Null(second.EventReaderTask);

        supervisor.Kill(a);
        supervisor.Kill(b);
    }

    [Fact]
    public async Task Handshake_emits_exactly_one_session_started_event()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();

        var drained = new List<RunnerEvent>();
        using var drainCts = new CancellationTokenSource();
        var drainLoop = Task.Run(async () =>
        {
            await foreach (var item in _ring.ReadAllAsync(drainCts.Token))
                lock (drained) drained.Add(item.Event);
        });

        supervisor.Spawn(TestKit.Dispatch(task), TestKit.Profile("emit-stream"), "machine-term");

        Assert.True(
            await TestKit.WaitUntilAsync(() => SessionEvents(drained).Count >= 1, TimeSpan.FromSeconds(20)),
            "supervisor never emitted a session-started event");

        supervisor.Kill(task);
        _ring.Complete();
        await drainLoop;

        var evt = Assert.Single(SessionEvents(drained));
        Assert.Equal(task, evt.Task);
        Assert.False(string.IsNullOrWhiteSpace(evt.SessionRef));
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
