using System.Diagnostics;
using Docket.HarnessSupport;

namespace Docket.Runner.TestHarness;

/// <summary>
/// A minimal, cross-platform stand-in for a real harness that the runner tests
/// spawn as a real process — no shell, argv only, matching docketd's own
/// constraint (§10). It writes markers into its working directory (which the
/// supervisor sets to <c>{work_root}/{session_id}</c>), so a test can observe env
/// injection, working directory, stop delivery, process-tree membership, its own OS
/// pid, and the dead-man's switch without depending on any installed harness.
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
///   emit-both       — like emit-stream, but also write known lines to stderr after the
///                     stdout fixture. Exercises §12 transcript capture: the supervisor
///                     tees stdout to the .ndjson transcript and captures stderr to the
///                     .stderr file while stdout still maps to events (tee, not divert).
///   exit-code [n]   — exit immediately with n (default <see cref="ServiceExitCode"/>).
///                     For §10 service supervision, where the test needs a process that
///                     really starts and really exits with a known code. The obvious
///                     `/bin/sh -c "exit 3"` is POSIX-only: on Windows it cannot start at
///                     all, so nothing runs and there is no exit code to observe — a
///                     failed spawn masquerading as a failed run.
///   echo-stdin      — echo each stdin line to stdout as `got: &lt;line&gt;`, so a §10
///                     write_process test can prove the bytes arrived by reading the log.
///   echo-argv       — write the argv this process received (one token per line,
///                     including this mode word at [0]) to an atomic `argv` marker,
///                     then watch stdin like `run`. Lets the §11 resume tests assert
///                     the supervisor built the spawn argv from resume.args with the
///                     {session_id}/{mcp_config} placeholders substituted.
///   http-serve p t  — bind loopback port p and answer every request with body t
///                     (HTTP/1.1, Connection: close), until killed. Does NOT watch
///                     stdin, because an agent-started process (§10 `start_process`)
///                     gets a closed stdin by default and would otherwise see EOF and
///                     exit at once. The §8.2/§8.3 scenarios spawn this as the thing a
///                     worker registers and a consumer on another machine fetches
///                     through the relay, so it has to speak enough HTTP for `curl`.
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

    /// <summary>Verbatim stderr lines the <c>emit-both</c> mode writes after the stdout
    /// fixture — the §12 capture test asserts they land in the task's <c>.stderr</c>
    /// transcript exactly, while the stdout stream-json still tees and maps to events.</summary>
    public static readonly string[] EmitBothStderrLines =
    [
        "docket-harness: warming up (stderr)",
        "docket-harness: a noisy diagnostic line",
    ];

    /// <summary>
    /// Exit code the dead-man's switch takes on stdin EOF. 66 == sysexits'
    /// <c>EX_NOINPUT</c> ("cannot open input") — thematically, our input pipe went
    /// away. The runner tests assert on it to prove the switch (not a signal or a
    /// graceful stop) ended us.
    /// </summary>
    public const int DeadManExitCode = 66;

    /// <summary>
    /// Default exit code for the <c>exit-code</c> mode, so a service-supervision test can
    /// assert against this rather than a literal — the same convention
    /// <see cref="DeadManExitCode"/> follows. 3 is arbitrary but deliberately not 0 or 1:
    /// it cannot be confused with a clean exit or with a generic failure the runtime might
    /// have produced on its own.
    /// </summary>
    public const int ServiceExitCode = 3;

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

        // §10: an ACP agent that speaks the protocol and nothing else — no MCP client, no
        // model. It exists so the supervision-level E2E can assert on how a session OPENED
        // (new vs load, and with which ref), which under stream mode was observable in a
        // respawned argv and now happens on a connection nobody else sees. AcpAgentLoop
        // writes acp_session.json; the work body has nothing to do but succeed.
        if (mode == "--acp" || args.Contains("--acp", StringComparer.Ordinal))
            return await AcpAgentLoop.RunAsync(
                Directory.GetCurrentDirectory(),
                (_, _, _, _) => Task.FromResult(0),
                CancellationToken.None,
                sessionId: EmitStreamSessionId);
        var cwd = Directory.GetCurrentDirectory();

        switch (mode)
        {
            case "echo-stdin":
                // §10 write_process: echo each stdin line to stdout, so a test can prove a write
                // reached the pipe by reading the captured log — which is exactly the loop an
                // agent uses, since a pipe carries no reply channel of its own.
                {
                    string? line;
                    while ((line = await Console.In.ReadLineAsync()) is not null)
                    {
                        await Console.Out.WriteLineAsync($"got: {line}");
                        await Console.Out.FlushAsync();
                    }
                    return 0;
                }

            case "exit-code":
                // Exits immediately with the code in args[1] (default 3). Exists because
                // the obvious way to write this — `/bin/sh -c "exit 3"` — is a POSIX-only
                // spawn that cannot start on Windows at all, so the process never runs and
                // there is no exit code to observe. Spawning this harness instead means the
                // process genuinely starts and genuinely exits with the asked-for code on
                // every platform, which is what a service-supervision test needs to assert.
                return args.Length > 1 && int.TryParse(args[1], out var code) ? code : ServiceExitCode;

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

            case "emit-both":
                await WriteStartedAsync(cwd);
                // §12 capture: emit the same stream-json fixture to stdout (so events
                // still map under EventsSource.Terminal) AND known lines to stderr, then
                // watch stdin like `run`. Lets a test prove the supervisor tees stdout to
                // the .ndjson transcript and captures stderr to the .stderr file, verbatim.
                await EmitStreamFixtureAsync();
                foreach (var line in EmitBothStderrLines)
                    await Console.Error.WriteLineAsync(line);
                await Console.Error.FlushAsync();
                return await WatchStdinAsync(cwd, grandchildren: [], onLine: null);

            case "echo-env":
                // §10 telemetry ingest: record the environment we were actually spawned
                // with, one KEY=value per line, so a test can prove exactly which
                // variables docketd set (and, just as load-bearing, which it did not).
                // Then watch stdin like `run` so the dead-man pipe governs lifetime.
                await WriteMarkerAtomicAsync(Path.Combine(cwd, "env"), EnvironmentLines());
                return await WatchStdinAsync(cwd, grandchildren: [], onLine: null);

            case "hook-ok":
                // Profile hook: record argv and exit 0 immediately (do not watch stdin).
                await WriteMarkerAtomicAsync(Path.Combine(cwd, "hook"), string.Join('\n', args));
                return 0;

            case "hook-fail":
                return 1;

            case "hook-env":
                // Profile hook: record the environment docketd actually handed us, then
                // exit. Proves DOCKET_SESSION_ID / DOCKET_WORKER_TOKEN were stripped.
                await WriteMarkerAtomicAsync(Path.Combine(cwd, "hook-env"), EnvironmentLines());
                return 0;

            case "echo-argv":
                // §11 resume: record the argv we were actually spawned with — one
                // token per line, this mode word at [0] — so the test can prove the
                // supervisor substituted {session_id}/{mcp_config} into resume.args.
                // Then watch stdin like `run` so the dead-man pipe governs lifetime.
                await WriteMarkerAtomicAsync(Path.Combine(cwd, "argv"), string.Join('\n', args));
                return await WatchStdinAsync(cwd, grandchildren: [], onLine: null);

            case "http-serve":
                // A long-lived listener an agent starts through §10 `start_process`. Stdin is
                // closed for such a process by default, so this mode deliberately does not
                // watch it — PDEATHSIG (armed above) plus docketd's stop/stray paths are what
                // end it. Serves until killed; a dead port would make the consumer's forward
                // indistinguishable from a crashed service, so it never exits on its own.
                return await ServeHttpAsync(
                    port: int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture),
                    body: args[2]);

            default: // "run"
                await WriteStartedAsync(cwd);
                return await WatchStdinAsync(cwd, grandchildren: [], onLine: null);
        }
    }

    /// <summary>
    /// Answers every accepted connection with <paramref name="body"/> over minimal
    /// HTTP/1.1 and never returns. Enough of the protocol for <c>curl</c> — request line
    /// and headers drained to the blank line, then a fixed-length response with
    /// <c>Connection: close</c> — and nothing more: this stands in for "the dev server a
    /// worker started", so what matters is that the bytes are reachable through a §8.3
    /// forward, not that it is a web server.
    /// </summary>
    private static async Task<int> ServeHttpAsync(int port, string body)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(body);
        var response = System.Text.Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");

        // Loopback only (§8.2: a registered endpoint is reached through the relay, never
        // by exposing a port to the network).
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        listener.Start();
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            // One connection at a time is plenty for a probe-and-fetch consumer, and it
            // keeps the failure mode legible: a hang means nothing answered, never that a
            // pool was exhausted.
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        await DrainRequestHeadAsync(stream);
                        await stream.WriteAsync(response);
                        await stream.WriteAsync(payload);
                        await stream.FlushAsync();
                    }
                    catch (IOException) { /* the client hung up; the next one is unaffected */ }
                    catch (System.Net.Sockets.SocketException) { /* likewise */ }
                }
            });
        }
    }

    /// <summary>
    /// Reads request bytes until the blank line that ends the head (or EOF), so the
    /// response is written to a client that has finished asking. Bounded — a peer that
    /// never sends the terminator must not wedge the connection task forever.
    /// </summary>
    private static async Task DrainRequestHeadAsync(Stream stream)
    {
        var buffer = new byte[1024];
        var seen = 0;
        var terminator = 0; // how much of "\r\n\r\n" has matched so far
        while (seen < 16 * 1024)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
                return;
            seen += read;
            for (var i = 0; i < read; i++)
            {
                var expected = (terminator % 2 == 0) ? (byte)'\r' : (byte)'\n';
                terminator = buffer[i] == expected ? terminator + 1 : (buffer[i] == '\r' ? 1 : 0);
                if (terminator == 4)
                    return;
            }
        }
    }

    /// <summary>
    /// The whole spawn environment as <c>KEY=value</c> lines, sorted so a failure
    /// diff is readable. A value containing a newline (nothing docketd sets does,
    /// but the inherited environment is the operator's) would break the one-per-line
    /// shape, so those collapse to spaces.
    /// </summary>
    private static string EnvironmentLines()
    {
        var lines = new List<string>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var value = entry.Value?.ToString() ?? "";
            lines.Add($"{entry.Key}={value.ReplaceLineEndings(" ")}");
        }
        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines);
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
        var sessionId = Environment.GetEnvironmentVariable("DOCKET_SESSION_ID") ?? "none";
        var machineId = Environment.GetEnvironmentVariable("DOCKET_MACHINE_ID") ?? "none";
        // Our own OS pid, written BEFORE the `started` marker so a test that polls for
        // `started` and then reads this can never miss it. The §17.8 chaos suite needs it to
        // assert that a kill the plane ordered actually took this process down: every other
        // marker records something the process CHOSE to write, and a killed process writes
        // nothing, so the pid is the only handle on "it is gone" as opposed to "it stopped
        // saying anything". Redispatch overwrites it, so a test wanting this generation's pid
        // reads it before the requeue.
        await WriteMarkerAtomicAsync(Path.Combine(cwd, "pid"), Environment.ProcessId.ToString());
        await WriteMarkerAtomicAsync(Path.Combine(cwd, "started"), $"{sessionId}\n{machineId}");
    }

    /// <summary>
    /// Writes a marker atomically so the harnessing test — which polls
    /// <c>File.Exists</c> and then reads the content — sees either no file or the
    /// complete content, never the create-then-write gap.
    /// <see cref="File.WriteAllTextAsync(string,string?,CancellationToken)"/> truncates then streams,
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
