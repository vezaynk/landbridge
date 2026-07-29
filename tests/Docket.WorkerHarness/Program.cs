using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Docket.HarnessSupport;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Docket.WorkerHarness;

/// <summary>
/// The walking skeleton's scripted worker (spec §10, worker-skill.md) — a REAL
/// process speaking REAL MCP back to the control plane, with no LLM. It stands in
/// for <c>claude -p</c> in the automated proof: it does exactly what a dispatched
/// worker's opening moves are and nothing else.
///
/// It reads the MCP client config <c>docketd</c> injected (§13) — the
/// <c>--mcp-config</c> path <see cref="ProcessSupervisor"/> substituted into its
/// argv, pointing at <c>{work_dir}/mcp.json</c> — connects to the plane with the
/// dispatched worker token as a bearer credential, and calls <c>get_task</c> to
/// learn its assignment. The raw <c>get_task</c> response is written to
/// <c>./get_task.json</c> in the work dir so the harnessing test can assert the
/// assignment crossed the wire intact.
///
/// <para>What it does next is driven by the task's opaque prose <b>description</b>
/// (§7) — the natural channel for telling a worker what to do without new protocol:
/// <list type="bullet">
/// <item><c>relay-serve:&lt;name&gt;</c> — bind a loopback TCP echo server, advertise
/// it via <c>register_service</c>, and stay running (never reporting a result, so the
/// task stays <c>working</c> and the service remains forwardable) until the parent
/// dies or it is killed. The bound port is written to <c>./service.json</c>.</item>
/// <item><c>relay-consume:&lt;name&gt;</c> — <c>open_forward</c> that service, connect
/// to the returned loopback address, prove a byte round-trip through the real relay,
/// then <c>report_result("relay-echo:ok:&lt;bytes&gt;")</c>.</item>
/// <item>anything else — the default skeleton move: <c>report_result</c> a reference
/// and exit 0 (drives working → verifying).</item>
/// </list></para>
///
/// No shell, argv only (§10 convention). Any failure writes a diagnostic to
/// <c>./harness_error.txt</c> and exits non-zero so the test sees a hard failure
/// rather than a hang.
/// </summary>
public static class Program
{
    /// <summary>A description with this prefix makes the worker serve an echo service (§8.3 fleet E2E).</summary>
    private const string ServePrefix = "relay-serve:";

    /// <summary>A description with this prefix makes the worker open a forward and prove the byte path.</summary>
    private const string ConsumePrefix = "relay-consume:";

    public static async Task<int> Main(string[] args)
    {
        var cwd = Directory.GetCurrentDirectory();
        // No fixed run deadline: relay-serve mode stays up for the life of the task
        // (until the parent dies or it is killed), so the only lifetime bound is the
        // stdin-EOF watch + PDEATHSIG below. The short-lived default/consume flows
        // bound their own MCP calls.
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Dead-man's switch (§10). Linux: SIGKILL us if docketd (our parent) dies.
        if (ParentDeathSignal.ArmAndParentAlreadyDead())
            return 1;
        // Portable: docketd holds the write end of our stdin for our lifetime, so
        // EOF means docketd is gone. Watch it in the background and cancel the run —
        // the worker's only channel to the plane is MCP, so stdin carries no data,
        // just liveness. Cancellation unwinds the work below into a non-zero exit.
        // This is what bounds relay-serve mode's otherwise-open lifetime.
        _ = WatchStdinForEofAsync(cts);

        // §1 tracing: root the worker's span on the traceparent docketd injected
        // (DOCKET_TRACEPARENT), so this process — and its MCP calls back to the
        // plane — continue the one trace that began at the Lead's create_task. The
        // root span stays current for the whole run; disposed last so it (and, when
        // a collector is configured, the export) flushes before the process exits.
        using var telemetry = WorkerTelemetry.Start(cwd);

        try
        {
            var (url, authorization) = ResolveConnection(args);

            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(url),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = authorization },
            });

            // Bound the connect + get_task handshake so a stuck setup fails hard
            // rather than hanging; the per-mode work below sets its own bounds.
            using var setupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            setupCts.CancelAfter(TimeSpan.FromSeconds(60));
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: setupCts.Token);

            // ── Learn the assignment (§7): namespace, description, criteria, workspace, attempt.
            var assignment = await client.CallToolAsync(
                "get_task", new Dictionary<string, object?>(), cancellationToken: setupCts.Token);
            if (assignment.IsError == true)
                throw new InvalidOperationException("get_task returned an error: " + TextOf(assignment));

            var assignmentJson = TextOf(assignment);
            await File.WriteAllTextAsync(Path.Combine(cwd, "get_task.json"), assignmentJson, ct);

            // ── Branch on the description (§7): the opaque prose is the channel that
            //    tells the worker what to do, so one 'default' profile drives a whole
            //    fleet — a serving producer and a forwarding consumer — off the wire.
            var description = DescriptionOf(assignmentJson);
            if (description is not null && description.StartsWith(ServePrefix, StringComparison.Ordinal))
                return await RunServeModeAsync(client, cwd, description[ServePrefix.Length..].Trim(), ct);
            if (description is not null && description.StartsWith(ConsumePrefix, StringComparison.Ordinal))
                return await RunConsumeModeAsync(client, cwd, description[ConsumePrefix.Length..].Trim(), ct);

            // ── Default (§10) — report a result, driving working → verifying. No real
            //    work; a reference is all the state machine requires (verification is separate).
            var reported = await client.CallToolAsync(
                "report_result",
                new Dictionary<string, object?> { ["resultReference"] = "docket-worker-harness:done" },
                cancellationToken: setupCts.Token);
            if (reported.IsError == true)
                throw new InvalidOperationException("report_result returned an error: " + TextOf(reported));

            return 0;
        }
        catch (Exception e)
        {
            try { await File.WriteAllTextAsync(Path.Combine(cwd, "harness_error.txt"), e.ToString(), CancellationToken.None); }
            catch { /* best effort */ }
            return 1;
        }
    }

    /// <summary>
    /// <c>relay-serve:&lt;name&gt;</c> (§8.3): bind a loopback TCP echo server, register
    /// it under <paramref name="serviceName"/>, then stay running — serving echo until
    /// the dead-man watch / kill cancels us. It <b>never reports a result</b>: the task
    /// must stay <c>working</c> for the store to keep the service row and for it to be
    /// forwardable. The bound port is written to <c>./service.json</c> atomically so the
    /// harnessing test can read it without catching a partial write.
    /// </summary>
    private static async Task<int> RunServeModeAsync(
        McpClient client, string cwd, string serviceName, CancellationToken ct)
    {
        // Bind BEFORE registering (§8.2: advertise only a port you have bound).
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            var echoLoop = EchoAcceptLoopAsync(listener, ct);

            using (var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                opCts.CancelAfter(TimeSpan.FromSeconds(30));
                var registered = await client.CallToolAsync(
                    "register_service",
                    new Dictionary<string, object?> { ["name"] = serviceName, ["port"] = boundPort },
                    cancellationToken: opCts.Token);
                if (registered.IsError == true)
                    throw new InvalidOperationException("register_service returned an error: " + TextOf(registered));
            }

            // Diagnostics marker (atomic — temp sibling + rename, §10 harness convention).
            await WriteMarkerAtomicAsync(
                Path.Combine(cwd, "service.json"),
                new JsonObject { ["service"] = serviceName, ["port"] = boundPort }.ToJsonString());

            // Serve until torn down. Not reporting a result is deliberate (see summary).
            try { await echoLoop; }
            catch (OperationCanceledException) { /* stdin-EOF / kill tore us down */ }
            return 0;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// <c>relay-consume:&lt;name&gt;</c> (§8.3): <c>open_forward</c> the service, connect
    /// to the returned loopback address, write a random probe and verify it round-trips
    /// back byte-for-byte through the real relay, then report <c>relay-echo:ok:&lt;bytes&gt;</c>.
    /// Any failure throws, so the caller writes <c>harness_error.txt</c> and exits non-zero
    /// (reporting nothing) — the test sees a hard fail rather than a false success.
    /// </summary>
    private static async Task<int> RunConsumeModeAsync(
        McpClient client, string cwd, string serviceName, CancellationToken ct)
    {
        using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        opCts.CancelAfter(TimeSpan.FromSeconds(60));
        var opCt = opCts.Token;

        var opened = await client.CallToolAsync(
            "open_forward",
            new Dictionary<string, object?> { ["serviceName"] = serviceName },
            cancellationToken: opCt);
        if (opened.IsError == true)
            throw new InvalidOperationException("open_forward returned an error: " + TextOf(opened));

        var (host, port) = ForwardAddressOf(TextOf(opened));

        // Prove the byte path: consumer docketd → relay → producer docketd → echo.
        var payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Parse(host), port, opCt);
            await using var stream = tcp.GetStream();
            await stream.WriteAsync(payload, opCt);
            var echoed = await ReadExactlyAsync(stream, payload.Length, opCt);
            if (!payload.AsSpan().SequenceEqual(echoed))
                throw new InvalidOperationException(
                    $"echo mismatch: {payload.Length} bytes sent, round-trip differed through the relay");
        }

        var reported = await client.CallToolAsync(
            "report_result",
            new Dictionary<string, object?> { ["resultReference"] = $"relay-echo:ok:{payload.Length}" },
            cancellationToken: opCt);
        if (reported.IsError == true)
            throw new InvalidOperationException("report_result returned an error: " + TextOf(reported));

        // A diagnostics breadcrumb mirroring the serve side; not load-bearing.
        await WriteMarkerAtomicAsync(
            Path.Combine(cwd, "consume.json"),
            new JsonObject { ["service"] = serviceName, ["bytes"] = payload.Length }.ToJsonString());
        return 0;
    }

    /// <summary>The task's prose description from a <c>get_task</c> response, or null if absent/malformed.</summary>
    private static string? DescriptionOf(string assignmentJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(assignmentJson);
            return doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The <c>{host, port}</c> from an <c>open_forward</c> response (snake_case, §8.3).</summary>
    private static (string Host, int Port) ForwardAddressOf(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var host = root.GetProperty("host").GetString()
            ?? throw new InvalidOperationException("open_forward response missing host");
        return (host, root.GetProperty("port").GetInt32());
    }

    /// <summary>Accept loop for the serve-mode echo service; one echo task per connection.</summary>
    private static async Task EchoAcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync(ct);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return; // torn down
            }
            _ = EchoConnectionAsync(socket, ct);
        }
    }

    private static async Task EchoConnectionAsync(Socket socket, CancellationToken ct)
    {
        using (socket)
        await using (var stream = new NetworkStream(socket, ownsSocket: false))
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, ct);
                    if (read == 0)
                        return;
                    await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            catch (Exception) { /* peer closed / torn down */ }
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0)
                throw new InvalidOperationException($"stream closed after {offset}/{count} bytes");
            offset += read;
        }
        return buffer;
    }

    /// <summary>
    /// Writes a marker atomically (temp sibling + rename) so the harnessing test —
    /// which polls <c>File.Exists</c> then reads — sees either no file or the whole
    /// content, never a create-then-write gap. The §10 harness convention.
    /// </summary>
    private static async Task WriteMarkerAtomicAsync(string path, string content)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Portable dead-man watch: reads stdin to EOF and cancels <paramref name="cts"/>
    /// when it arrives. docketd holds the write end for our lifetime, so EOF is its
    /// death; any bytes are ignored (MCP, not stdin, is the worker's channel). On a
    /// normal run the process exits before this ever completes.
    /// </summary>
    private static async Task WatchStdinForEofAsync(CancellationTokenSource cts)
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var reader = new StreamReader(stdin);
            while (await reader.ReadLineAsync(cts.Token) is not null) { }
        }
        catch (OperationCanceledException)
        {
            return; // the run finished and cancelled us — not a dead-man event
        }
        catch
        {
            // fall through: a read failure is treated the same as EOF
        }

        if (!cts.IsCancellationRequested)
            cts.Cancel();
    }

    /// <summary>
    /// Resolves the plane URL and Authorization header. Primary source is the
    /// injected <c>--mcp-config</c> file (proves the §13 injection path); falls
    /// back to <c>DOCKET_WORKER_TOKEN</c> + <c>DOCKET_MCP_URL</c> if no config
    /// path was passed.
    /// </summary>
    private static (string Url, string Authorization) ResolveConnection(string[] args)
    {
        var configPath = FlagValue(args, "--mcp-config");
        if (configPath is not null)
        {
            var root = JsonNode.Parse(File.ReadAllText(configPath))
                ?? throw new InvalidOperationException($"empty mcp config at {configPath}");
            // The first server under mcpServers — the plane, named "docket" by docketd.
            var servers = root["mcpServers"]?.AsObject()
                ?? throw new InvalidOperationException("mcp config has no mcpServers");
            var server = servers.First().Value
                ?? throw new InvalidOperationException("mcp config has no server entry");
            var url = (string?)server["url"]
                ?? throw new InvalidOperationException("mcp config server has no url");
            var authorization = (string?)server["headers"]?["Authorization"]
                ?? throw new InvalidOperationException("mcp config server has no Authorization header");
            return (url, authorization);
        }

        var token = Environment.GetEnvironmentVariable("DOCKET_WORKER_TOKEN")
            ?? throw new InvalidOperationException("no --mcp-config and no DOCKET_WORKER_TOKEN");
        var mcpUrl = Environment.GetEnvironmentVariable("DOCKET_MCP_URL")
            ?? throw new InvalidOperationException("no --mcp-config and no DOCKET_MCP_URL");
        return (mcpUrl, $"Bearer {token}");
    }

    private static string? FlagValue(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
                return args[i + 1];
        return null;
    }

    private static string TextOf(CallToolResult result)
    {
        // A typed tool return (get_task) also surfaces as structured content; a
        // string return (report_result) is a text block. Prefer structured, fall
        // back to the joined text blocks.
        if (result.StructuredContent is { } structured)
            return structured.GetRawText();
        return string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
    }
}

/// <summary>
/// The worker's OpenTelemetry setup (§1). Roots a span on <c>DOCKET_TRACEPARENT</c>
/// so the worker sits inside the trace that began at the Lead's <c>create_task</c>,
/// and — when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set — exports it plus the MCP
/// calls it makes (HttpClient instrumentation nests them and injects the
/// traceparent, so the plane continues the trace on <c>get_task</c>/<c>report_result</c>).
///
/// When no collector is configured (e.g. the deterministic continuity test) a bare
/// <see cref="ActivityListener"/> still makes the root span sample, so its trace id
/// is real and lands in the <c>trace.json</c> marker the test asserts on.
/// </summary>
internal sealed class WorkerTelemetry : IDisposable
{
    private const string SourceName = "Docket.WorkerHarness";

    private readonly Activity? _rootActivity;
    private readonly TracerProvider? _tracerProvider;
    private readonly ActivityListener? _listener;
    private readonly ActivitySource _source;

    private WorkerTelemetry(
        ActivitySource source, Activity? root, TracerProvider? tracer, ActivityListener? listener)
    {
        _source = source;
        _rootActivity = root;
        _tracerProvider = tracer;
        _listener = listener;
    }

    public static WorkerTelemetry Start(string workDir)
    {
        var source = new ActivitySource(SourceName);

        ActivityContext parentContext = default;
        var traceparent = Environment.GetEnvironmentVariable("DOCKET_TRACEPARENT");
        if (!string.IsNullOrWhiteSpace(traceparent))
            ActivityContext.TryParse(traceparent, null, out parentContext);

        TracerProvider? tracer = null;
        ActivityListener? listener = null;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")))
        {
            tracer = Sdk.CreateTracerProviderBuilder()
                .AddSource(SourceName)
                .ConfigureResource(r => r.AddService("docket-worker"))
                .AddHttpClientInstrumentation()
                .AddOtlpExporter()
                .Build();
        }
        else
        {
            listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            };
            ActivitySource.AddActivityListener(listener);
        }

        var root = parentContext == default
            ? source.StartActivity("worker-run", ActivityKind.Internal)
            : source.StartActivity("worker-run", ActivityKind.Internal, parentContext);

        WriteTraceMarker(workDir, root, parentContext);
        return new WorkerTelemetry(source, root, tracer, listener);
    }

    /// <summary>
    /// Records the resolved trace id / span ids to <c>trace.json</c> in the work
    /// dir so the continuity test can assert the worker's span shares the Lead's
    /// trace id without needing a live collector.
    /// </summary>
    private static void WriteTraceMarker(string workDir, Activity? root, ActivityContext parentContext)
    {
        if (root is null)
            return;
        var marker = new JsonObject
        {
            ["traceId"] = root.TraceId.ToHexString(),
            ["spanId"] = root.SpanId.ToHexString(),
            ["parentSpanId"] = parentContext == default ? null : parentContext.SpanId.ToHexString(),
            ["traceparent"] = root.Id,
        };
        try { File.WriteAllText(Path.Combine(workDir, "trace.json"), marker.ToJsonString()); }
        catch { /* best effort — the marker is a test aid, not part of the run */ }
    }

    public void Dispose()
    {
        _rootActivity?.Dispose();
        if (_tracerProvider is not null)
        {
            _tracerProvider.ForceFlush(5000);
            _tracerProvider.Dispose();
        }
        _listener?.Dispose();
        _source.Dispose();
    }
}
