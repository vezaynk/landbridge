using System.Diagnostics;
using Docket.HarnessSupport;

namespace Docket.Runner.TestHarness;

/// <summary>
/// A minimal, cross-platform stand-in for a real harness that the runner tests
/// spawn as a real process — no shell, argv only, matching docketd's own
/// constraint (§10). It writes markers into its working directory (which the
/// supervisor sets to <c>{work_root}/{task_id}</c>), so a test can observe env
/// injection, working directory, stop delivery, process-tree membership, and the
/// dead-man's switch without depending on any installed harness.
///
/// <para><b>Dead-man's switch (§10).</b> Every long-running mode watches stdin.
/// docketd holds the write end of that pipe for the worker's lifetime, so EOF
/// means docketd is gone: on EOF the harness kills any grandchild it spawned,
/// writes the atomic <c>deadman</c> marker, and exits with
/// <see cref="DeadManExitCode"/>. On Linux it additionally arms PDEATHSIG so a
/// hard docketd death is instantaneous (see <see cref="ParentDeathSignal"/>).</para>
///
/// Modes (argv[0]):
///   run             — write the started marker, then watch stdin until EOF/kill.
///   spawn-child     — like run, but first fork a grandchild sleeper and record
///                     its pid; the dead-man path kills that grandchild too.
///   stdin-stop      — like run, but a "stop" line winds down gracefully (exit 0);
///                     EOF still takes the dead-man path (§10/§11 wind-down).
///   child           — a bare sleeper (the grandchild); does not watch stdin.
///   middleman       — spawn an inner spawn-child harness holding its stdin, record
///                     the inner pid, then sleep. A stand-in for docketd for the
///                     true-parent-death test: SIGKILL the middleman and the inner
///                     sees EOF and fires its own switch.
///   middleman-nopipe— spawn a `sleeper` inner WITHOUT redirecting its stdin, so on
///                     Linux only PDEATHSIG can end it when the middleman dies —
///                     isolates PDEATHSIG for the CI-only test.
///   sleeper         — arm PDEATHSIG, write a `ready` marker, sleep; does NOT watch
///                     stdin (so the PDEATHSIG test cannot pass via stdin EOF).
///   emit-stream     — write a canned claude-style stream-json (NDJSON) transcript to
///                     stdout, flush, then watch stdin like `run`. Exercises the
///                     supervisor's EventsSource.Terminal stdout drain: the reader
///                     maps the transcript to ToolCallEvents and per-task liveness.
///                     Waiting on stdin keeps the dead-man pipe governing lifetime.
///   echo-argv       — write the argv this process received (one token per line,
///                     including this mode word at [0]) to an atomic `argv` marker,
///                     then watch stdin like `run`. Lets the §11 resume tests assert
///                     the supervisor built the spawn argv from resume.args with the
///                     {session_id}/{mcp_config} placeholders substituted.
/// </summary>
public static class Program
{
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromSeconds(120);

    /// <summary>Session id the <c>emit-stream</c> fixture reports in its <c>system/init</c>
    /// line — the Terminal supervisor test asserts the reader captured exactly this.</summary>
    public const string EmitStreamSessionId = "sess-emit-stream";

    /// <summary>Tool names the <c>emit-stream</c> fixture's <c>tool_use</c> blocks carry,
    /// in order — the supervisor test asserts ToolCallEvents drain in this sequence.</summary>
    public static readonly string[] EmitStreamToolNames = ["Bash", "Read"];

    /// <summary>
    /// Exit code the dead-man's switch takes on stdin EOF. 66 == sysexits'
    /// <c>EX_NOINPUT</c> ("cannot open input") — thematically, our input pipe went
    /// away. The runner tests assert on it to prove the switch (not a signal or a
    /// graceful stop) ended us.
    /// </summary>
    public const int DeadManExitCode = 66;

    public static async Task<int> Main(string[] args)
    {
        // Dead-man's switch, Linux half (§10): SIGKILL us the moment docketd (our
        // direct parent) dies. Armed by every mode — including `child`, so the whole
        // test chain collapses promptly on Linux. If the parent already died, honour
        // the switch at once. A no-op off Linux; the stdin-EOF watch is the portable
        // half that runs on every OS.
        if (ParentDeathSignal.ArmAndParentAlreadyDead())
            return DeadManExitCode;

        var mode = args.Length > 0 ? args[0] : "run";
        var cwd = Directory.GetCurrentDirectory();

        switch (mode)
        {
            case "child":
                // A bare grandchild: it does NOT watch stdin. On the dead-man path
                // the parent harness kills it explicitly (portable); on Linux its
                // own PDEATHSIG catches a hard parent death too.
                await Task.Delay(MaxLifetime);
                return 0;

            case "sleeper":
                // PDEATHSIG-isolation stand-in: arm (done above), announce readiness,
                // then sleep WITHOUT watching stdin — so the only thing that can end
                // us when our parent dies is PDEATHSIG.
                await WriteMarkerAtomicAsync(Path.Combine(cwd, "ready"), "armed");
                await Task.Delay(MaxLifetime);
                return 0;

            case "spawn-child":
                await WriteStartedAsync(cwd);
                using (var grandchild = SpawnSelf("child"))
                {
                    await WriteMarkerAtomicAsync(Path.Combine(cwd, "child.pid"), grandchild.Id.ToString());
                    return await WatchStdinAsync(cwd, [grandchild], onLine: null);
                }

            case "stdin-stop":
                await WriteStartedAsync(cwd);
                // Message-mode stop and the dead-man switch share ONE stdin reader:
                // a "stop" line winds us down gracefully; EOF fires the switch.
                return await WatchStdinAsync(cwd, grandchildren: [], onLine: async line =>
                {
                    if (line.Contains("stop", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteMarkerAtomicAsync(Path.Combine(cwd, "stopped"), line);
                        return StdinAction.GracefulStop;
                    }
                    return StdinAction.Continue;
                });

            case "middleman":
                // Stand-in for docketd (true-parent-death rig): spawn an inner
                // spawn-child harness holding ITS stdin exactly as docketd would,
                // record the inner pid, then sleep. When the test SIGKILLs us the OS
                // closes that pipe → the inner sees EOF and fires its dead-man switch.
                using (var inner = SpawnSelf("spawn-child", cwd, holdStdin: true))
                {
                    await WriteMarkerAtomicAsync(Path.Combine(cwd, "inner.pid"), inner.Id.ToString());
                    await Task.Delay(MaxLifetime);
                }
                return 0;

            case "middleman-nopipe":
                // PDEATHSIG-isolation rig: spawn a `sleeper` inner but do NOT hold a
                // pipe on its stdin, so a parent death gives it no EOF. On Linux only
                // PDEATHSIG can end it when we die.
                using (var inner = SpawnSelf("sleeper", cwd, holdStdin: false))
                {
                    await WriteMarkerAtomicAsync(Path.Combine(cwd, "inner.pid"), inner.Id.ToString());
                    await Task.Delay(MaxLifetime);
                }
                return 0;

            case "emit-stream":
                await WriteStartedAsync(cwd);
                // Emit the fixture transcript to stdout (the supervisor redirects and
                // drains it under EventsSource.Terminal), flush, then behave like
                // `run`: watch stdin so the dead-man pipe still governs our lifetime.
                await EmitStreamFixtureAsync();
                return await WatchStdinAsync(cwd, grandchildren: [], onLine: null);

            case "echo-argv":
                // §11 resume: record the argv we were actually spawned with — one
                // token per line, this mode word at [0] — so the test can prove the
                // supervisor substituted {session_id}/{mcp_config} into resume.args.
                // Then watch stdin like `run` so the dead-man pipe governs lifetime.
                await WriteMarkerAtomicAsync(Path.Combine(cwd, "argv"), string.Join('\n', args));
                return await WatchStdinAsync(cwd, grandchildren: [], onLine: null);

            default: // "run"
                await WriteStartedAsync(cwd);
                return await WatchStdinAsync(cwd, grandchildren: [], onLine: null);
        }
    }

    /// <summary>
    /// Writes a small claude <c>--output-format stream-json</c>-shaped transcript,
    /// one JSON object per line, to stdout. It covers the shapes the Terminal
    /// reader maps: a <c>system/init</c> carrying <see cref="EmitStreamSessionId"/>,
    /// a thinking-only assistant turn (liveness, no tool call), single- and
    /// multi-block assistant turns whose <c>tool_use</c> blocks name
    /// <see cref="EmitStreamToolNames"/>, a stray non-JSON banner line the reader
    /// must skip, a tool_result turn, and a final <c>result</c>.
    /// </summary>
    private static async Task EmitStreamFixtureAsync()
    {
        // Plain raw literals (JSON braces would collide with raw-string
        // interpolation). The session id and tool names are hardcoded to match
        // EmitStreamSessionId / EmitStreamToolNames, asserted below so the two
        // never drift.
        string[] lines =
        [
            """{"type":"system","subtype":"init","session_id":"sess-emit-stream","tools":["Bash","Read"],"model":"claude-test"}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"planning the work"}]}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"ls"}}]}}""",
            "a stray non-JSON banner line the reader must skip",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_2","name":"Read","input":{"file":"x"}},{"type":"text","text":"reading now"}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"ok"}]}}""",
            """{"type":"result","subtype":"success","session_id":"sess-emit-stream","is_error":false,"num_turns":2}""",
        ];

        // Guard the hardcoded/const agreement (single source in spirit).
        if (EmitStreamSessionId != "sess-emit-stream"
            || EmitStreamToolNames is not ["Bash", "Read"])
            throw new InvalidOperationException("emit-stream fixture drifted from its published constants");

        foreach (var line in lines)
            await Console.Out.WriteLineAsync(line);
        await Console.Out.FlushAsync();
    }

    private enum StdinAction { Continue, GracefulStop }

    /// <summary>
    /// The portable half of the dead-man's switch. Reads stdin to end: docketd
    /// holds the write end for our lifetime, so EOF means docketd is gone. On EOF we
    /// kill every grandchild we spawned, write the atomic <c>deadman</c> marker, and
    /// exit with <see cref="DeadManExitCode"/>. <paramref name="onLine"/> lets a mode
    /// also honour injected lines (message-mode stop); returning
    /// <see cref="StdinAction.GracefulStop"/> exits 0 without firing the switch. A
    /// <see cref="MaxLifetime"/> cap keeps a wedged harness from lingering.
    /// </summary>
    private static async Task<int> WatchStdinAsync(
        string cwd, IReadOnlyList<Process> grandchildren, Func<string, Task<StdinAction>>? onLine)
    {
        using var lifetime = new CancellationTokenSource(MaxLifetime);
        using var stdin = Console.OpenStandardInput();
        using var reader = new StreamReader(stdin);
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(lifetime.Token)) is not null)
            {
                if (onLine is not null && await onLine(line) == StdinAction.GracefulStop)
                    return 0;
            }
        }
        catch (OperationCanceledException)
        {
            return 0; // hit the lifetime cap — a clean timeout, not a death
        }

        // EOF: the dead-man's switch. Take our grandchildren with us, then mark it.
        foreach (var grandchild in grandchildren)
            KillTree(grandchild);
        await WriteMarkerAtomicAsync(Path.Combine(cwd, "deadman"), "eof");
        return DeadManExitCode;
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // already exited, or the OS refused — either way it is gone
        }
    }

    private static async Task WriteStartedAsync(string cwd)
    {
        var taskId = Environment.GetEnvironmentVariable("DOCKET_TASK_ID") ?? "none";
        var machineId = Environment.GetEnvironmentVariable("DOCKET_MACHINE_ID") ?? "none";
        await WriteMarkerAtomicAsync(Path.Combine(cwd, "started"), $"{taskId}\n{machineId}");
    }

    /// <summary>
    /// Writes a marker atomically so the harnessing test — which polls
    /// <c>File.Exists</c> and then reads the content — sees either no file or the
    /// complete content, never the create-then-write gap.
    /// <see cref="File.WriteAllTextAsync(string,string?)"/> truncates then streams,
    /// so a concurrent reader can catch an empty or partial file; under heavy
    /// concurrent test load that raced the supervisor tests into an empty
    /// <c>child.pid</c> (FormatException) or a one-line <c>started</c>
    /// (IndexOutOfRange). Writing a temp sibling and renaming is atomic on POSIX
    /// and Windows (same directory = same volume), so a visible marker is always
    /// whole. Keep marker writes going through here.
    /// </summary>
    private static async Task WriteMarkerAtomicAsync(string path, string content)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Re-executes this harness binary in another mode. When
    /// <paramref name="holdStdin"/> is set the child's stdin is a pipe whose write
    /// end THIS process holds and never closes — the docketd convention — so our
    /// death closes it and trips the child's dead-man switch. Left at its default
    /// working directory unless <paramref name="cwd"/> is given, so an inner harness
    /// writes its markers where the test is watching.
    /// </summary>
    private static Process SpawnSelf(string mode, string? cwd = null, bool holdStdin = false)
    {
        var self = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot resolve own binary path");
        var psi = new ProcessStartInfo(self)
        {
            UseShellExecute = false,
            RedirectStandardInput = holdStdin,
        };
        if (cwd is not null)
            psi.WorkingDirectory = cwd;
        psi.ArgumentList.Add(mode);
        return Process.Start(psi) ?? throw new InvalidOperationException($"failed to spawn '{mode}' child");
    }
}
