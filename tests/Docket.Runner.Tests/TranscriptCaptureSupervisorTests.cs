using Docket.Core;
using Microsoft.Extensions.Time.Testing;
using HarnessProgram = Docket.Runner.TestHarness.Program;

namespace Docket.Runner.Tests;

/// <summary>
/// §12 transcript capture end-to-end through <see cref="ProcessSupervisor"/> against a
/// real spawned harness: capture tees the harness's stdout stream-json to a per-instance
/// <c>.ndjson</c> and its stderr to a <c>.stderr</c> file under the state dir, verbatim,
/// while the Terminal event drain keeps mapping the same stdout to events (tee, not
/// divert). Also pins capture-off (the default), per-instance file naming, the size cap
/// never killing the worker, and capture with no events source.
/// </summary>
public sealed class TranscriptCaptureSupervisorTests : IDisposable
{
    private readonly string _workRoot = TestKit.NewWorkRoot();
    private readonly string _stateDir = TestKit.NewWorkRoot();
    private readonly FakeTimeProvider _clock = new();
    private readonly OutboundEventRing _ring = new(capacity: 256);
    private ProcessSupervisor? _supervisor;
    private TranscriptStore? _store;

    private string TranscriptsRoot => Path.Combine(_stateDir, TranscriptDefaults.DirName);

    private TranscriptStore Store() =>
        _store ??= new TranscriptStore(TranscriptsRoot, TimeSpan.FromDays(7), _clock);

    private ProcessSupervisor Supervisor() =>
        _supervisor ??= new ProcessSupervisor(TestKit.Machine(_workRoot), _ring, _clock, taskReaper: null, transcripts: Store());

    private string TaskDir(TaskId task) => Path.Combine(TranscriptsRoot, task.ToString());

    [Fact]
    public async Task Capture_on_tees_stdout_and_captures_stderr_while_events_still_fire()
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

        supervisor.Spawn(
            TestKit.Dispatch(task),
            TestKit.Profile("emit-both", capture: true),
            "machine-cap");

        Assert.True(
            await TestKit.WaitUntilAsync(() => ToolNames(drained).Count >= 2, TimeSpan.FromSeconds(20)),
            "ACP tool-call events stopped firing while capturing");
        Assert.Equal(HarnessProgram.EmitStreamToolNames, ToolNames(drained));

        var stderr = Path.Combine(TaskDir(task), "0001.stderr");
        Assert.False(File.Exists(Path.Combine(TaskDir(task), "0001.ndjson")),
            "ACP stdout is the JSON-RPC pipe and must not be teed");

        // stderr is captured verbatim to its own file.
        Assert.True(
            await TestKit.WaitUntilAsync(
                () => File.Exists(stderr)
                      && TestKit.ReadLinesShared(stderr).Length >= HarnessProgram.EmitBothStderrLines.Length,
                TimeSpan.FromSeconds(20)),
            "stderr transcript never captured the expected lines");
        Assert.Equal(HarnessProgram.EmitBothStderrLines, TestKit.ReadLinesShared(stderr));

        supervisor.Kill(task);
        _ring.Complete();
        await drainLoop;
    }

    [Fact]
    public async Task Capture_off_by_default_writes_no_transcript_files()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();

        // Terminal events but capture OFF (the default): stdout is still redirected and
        // mapped exactly as before, and nothing is written under the state dir.
        supervisor.Spawn(
            TestKit.Dispatch(task),
            TestKit.Profile("emit-stream", events: EventsSource.Terminal),
            "machine-cap");

        // Prove the harness actually ran (its started marker in the work dir).
        var startedMarker = Path.Combine(_workRoot, task.ToString(), "started");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(startedMarker), TimeSpan.FromSeconds(15)),
            "harness never started");

        // Give any (erroneous) capture a chance to land, then assert none did.
        await Task.Delay(200);
        Assert.False(Directory.Exists(TaskDir(task)), "capture wrote files while disabled");

        supervisor.Kill(task);
    }

    [Fact]
    public async Task Each_worker_instance_gets_its_own_file_and_the_prior_stays_intact()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();

        // First instance.
        supervisor.Spawn(
            TestKit.Dispatch(task),
            TestKit.Profile("emit-both", capture: true),
            "machine-cap");
        var first = Path.Combine(TaskDir(task), "0001.stderr");
        Assert.True(
            await TestKit.WaitUntilAsync(
                () => File.Exists(first)
                      && TestKit.ReadLinesShared(first).Length >= HarnessProgram.EmitBothStderrLines.Length,
                TimeSpan.FromSeconds(20)),
            "first instance never wrote its stderr transcript");
        var firstContent = TestKit.ReadLinesShared(first);

        supervisor.Kill(task);
        Assert.True(await TestKit.WaitUntilAsync(() => supervisor.RunningTotal == 0, TimeSpan.FromSeconds(15)),
            "first instance never exited");

        supervisor.Spawn(
            TestKit.Dispatch(task),
            TestKit.Profile("emit-both", capture: true),
            "machine-cap");
        var second = Path.Combine(TaskDir(task), "0002.stderr");
        Assert.True(
            await TestKit.WaitUntilAsync(
                () => File.Exists(second)
                      && TestKit.ReadLinesShared(second).Length >= HarnessProgram.EmitBothStderrLines.Length,
                TimeSpan.FromSeconds(20)),
            "second instance never wrote its own stderr transcript");

        Assert.True(File.Exists(first), "the first instance's transcript was clobbered");
        Assert.Equal(firstContent, TestKit.ReadLinesShared(first));

        supervisor.Kill(task);
    }

    [Fact]
    public async Task Reaching_the_size_cap_truncates_the_file_without_killing_the_worker()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();

        // A tiny cap the fixture's stdout comfortably exceeds: the transcript stops with
        // a marker, but the worker keeps running — logging never kills the process.
        supervisor.Spawn(
            TestKit.Dispatch(task),
            TestKit.Profile("emit-both", capture: true, maxBytes: 20),
            "machine-cap");
        Assert.True(supervisor.TryGet(task, out var supervised));

        var stderr = Path.Combine(TaskDir(task), "0001.stderr");
        Assert.True(
            await TestKit.WaitUntilAsync(
                () => File.Exists(stderr) && TestKit.ReadLinesShared(stderr).Any(l => l.Contains("transcript_truncated")),
                TimeSpan.FromSeconds(20)),
            "the stderr transcript never hit its truncation marker");

        var lines = TestKit.ReadLinesShared(stderr);
        Assert.Contains("transcript_truncated", lines[^1]);

        // The worker is unaffected by the cap: still alive, reading stdin.
        Assert.True(supervised.ProcessAlive, "reaching the size cap must not kill the worker");

        supervisor.Kill(task);
    }

    [Fact]
    public async Task Capture_without_an_events_source_still_records_stdout()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();

        // events.source = none (no mapping), capture on: stdout is redirected and drained
        // solely to capture the transcript (the capture-only drain path).
        supervisor.Spawn(
            TestKit.Dispatch(task),
            TestKit.Profile("emit-both", capture: true),
            "machine-cap");

        var stderr = Path.Combine(TaskDir(task), "0001.stderr");
        Assert.True(
            await TestKit.WaitUntilAsync(
                () => File.Exists(stderr)
                      && TestKit.ReadLinesShared(stderr).Length >= HarnessProgram.EmitBothStderrLines.Length,
                TimeSpan.FromSeconds(20)),
            "stderr capture never recorded the harness output");
        Assert.Equal(HarnessProgram.EmitBothStderrLines, TestKit.ReadLinesShared(stderr));

        supervisor.Kill(task);
    }

    private static List<string> ToolNames(List<RunnerEvent> drained)
    {
        lock (drained)
            return drained.OfType<ToolCallEvent>().Select(e => e.Tool).ToList();
    }

    public void Dispose()
    {
        try { _supervisor?.KillAll(); } catch { /* best effort */ }
        TestKit.TryDeleteRoot(_workRoot);
        TestKit.TryDeleteRoot(_stateDir);
    }
}
