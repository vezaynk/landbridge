using Landbridge.Core;
using Microsoft.Extensions.Time.Testing;
using HarnessProgram = Landbridge.Runner.TestHarness.Program;

namespace Landbridge.Runner.Tests;

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

    private string TaskDir(SessionId task) => Path.Combine(TranscriptsRoot, task.ToString());

    [Fact]
    public async Task Capture_on_tees_stdout_and_captures_stderr_while_events_still_fire()
    {
        var task = SessionId.New();
        var supervisor = Supervisor();

        var drained = new List<RunnerEvent>();
        using var drainCts = new CancellationTokenSource();
        var drainLoop = Task.Run(async () =>
        {
            await foreach (var item in _ring.ReadAllAsync(drainCts.Token))
                lock (drained) drained.Add(item.Event);
        });

        // Capture on: stdout is teed to the transcript, stderr captured on its own drain.
        supervisor.Spawn(
            TestKit.Dispatch(task),
            TestKit.Profile("emit-both", capture: true),
            "machine-cap");

        // No event assertion here any more. Capture and event-mapping were two consumers of
        // one stdout read under stream mode, and this fact guarded against capture starving
        // the reader. Under ACP the drain IS the protocol client and this harness mode speaks
        // no ACP, so what remains to prove is the tee — which the transcript assertions below
        // do. That the ACP drain also tees while conversing is covered by the E2E tier, where
        // a real agent and real capture run together.

        var ndjson = Path.Combine(TaskDir(task), "0001.ndjson");
        var stderr = Path.Combine(TaskDir(task), "0001.stderr");

        // The full stream-json transcript is captured verbatim: all seven fixture lines,
        // including the session-init line and the stray non-JSON banner the event
        // mapper skips (capture is verbatim, mapping is selective). This reads the
        // transcript WHILE the worker (and the writer) is still alive — live-tailing is
        // a product behavior, so the read uses FileShare.ReadWrite (see ReadLinesShared)
        // and must work on every OS, Windows included.
        Assert.True(
            await TestKit.WaitUntilAsync(
                () => File.Exists(ndjson) && TestKit.ReadLinesShared(ndjson).Length >= 7, TimeSpan.FromSeconds(20)),
            "stdout transcript never reached the expected line count");
        var stdoutLines = TestKit.ReadLinesShared(ndjson);
        Assert.Equal(7, stdoutLines.Length);
        Assert.Contains(stdoutLines, l => l.Contains(HarnessProgram.EmitStreamSessionId));
        Assert.Contains(stdoutLines, l => l.Contains("a stray non-JSON banner"));

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
        var task = SessionId.New();
        var supervisor = Supervisor();

        // Terminal events but capture OFF (the default): stdout is still redirected and
        // mapped exactly as before, and nothing is written under the state dir.
        supervisor.Spawn(
            TestKit.Dispatch(task),
            TestKit.Profile("emit-stream"),
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
        var task = SessionId.New();
        var supervisor = Supervisor();

        // First instance.
        supervisor.Spawn(
            TestKit.Dispatch(task),
            TestKit.Profile("emit-stream", capture: true),
            "machine-cap");
        var first = Path.Combine(TaskDir(task), "0001.ndjson");
        Assert.True(
            await TestKit.WaitUntilAsync(() => File.Exists(first) && TestKit.ReadLinesShared(first).Length >= 7, TimeSpan.FromSeconds(20)),
            "first instance never wrote its transcript");
        var firstContent = TestKit.ReadLinesShared(first);

        // Tear it down and wait for the supervisor to let go (a requeue/resume is a new
        // instance from the supervisor's view).
        supervisor.Kill(task);
        Assert.True(await TestKit.WaitUntilAsync(() => supervisor.RunningTotal == 0, TimeSpan.FromSeconds(15)),
            "first instance never exited");

        // Second instance of the SAME task id → the next ordinal, predecessor untouched.
        supervisor.Spawn(
            TestKit.Dispatch(task),
            TestKit.Profile("emit-stream", capture: true),
            "machine-cap");
        var second = Path.Combine(TaskDir(task), "0002.ndjson");
        Assert.True(
            await TestKit.WaitUntilAsync(() => File.Exists(second) && TestKit.ReadLinesShared(second).Length >= 7, TimeSpan.FromSeconds(20)),
            "second instance never wrote its own transcript");

        Assert.True(File.Exists(first), "the first instance's transcript was clobbered");
        Assert.Equal(firstContent, TestKit.ReadLinesShared(first));

        supervisor.Kill(task);
    }

    [Fact]
    public async Task Reaching_the_size_cap_truncates_the_file_without_killing_the_worker()
    {
        var task = SessionId.New();
        var supervisor = Supervisor();

        // A tiny cap the fixture's stdout comfortably exceeds: the transcript stops with
        // a marker, but the worker keeps running — logging never kills the process.
        supervisor.Spawn(
            TestKit.Dispatch(task),
            TestKit.Profile("emit-both", capture: true, maxBytes: 200),
            "machine-cap");
        Assert.True(supervisor.TryGet(task, out var supervised));

        var ndjson = Path.Combine(TaskDir(task), "0001.ndjson");
        Assert.True(
            await TestKit.WaitUntilAsync(
                () => File.Exists(ndjson) && TestKit.ReadLinesShared(ndjson).Any(l => l.Contains("transcript_truncated")),
                TimeSpan.FromSeconds(20)),
            "the transcript never hit its truncation marker");

        var lines = TestKit.ReadLinesShared(ndjson);
        Assert.Contains("transcript_truncated", lines[^1]); // the marker is the final line
        Assert.True(lines.Length < 7, "capture did not stop before the end of the stream");

        // The worker is unaffected by the cap: still alive, reading stdin.
        Assert.True(supervised.ProcessAlive, "reaching the size cap must not kill the worker");

        supervisor.Kill(task);
    }

    [Fact]
    public async Task Capture_without_an_events_source_still_records_stdout()
    {
        var task = SessionId.New();
        var supervisor = Supervisor();

        // events.source = none (no mapping), capture on: stdout is redirected and drained
        // solely to capture the transcript (the capture-only drain path).
        supervisor.Spawn(
            TestKit.Dispatch(task),
            TestKit.Profile("emit-stream", capture: true),
            "machine-cap");

        var ndjson = Path.Combine(TaskDir(task), "0001.ndjson");
        Assert.True(
            await TestKit.WaitUntilAsync(
                () => File.Exists(ndjson) && TestKit.ReadLinesShared(ndjson).Length >= 7, TimeSpan.FromSeconds(20)),
            "capture-only stdout drain never recorded the transcript");
        var stdoutLines = TestKit.ReadLinesShared(ndjson);
        Assert.Equal(7, stdoutLines.Length);
        Assert.Contains(stdoutLines, l => l.Contains(HarnessProgram.EmitStreamSessionId));

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
