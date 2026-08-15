using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Docket.HarnessSupport;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Docket.CollabHarness;

/// <summary>
/// The scripted collaborator for the multi-machine E2E suite (spec §7/§8.3/§10) —
/// a REAL process speaking REAL MCP back to the control plane, with no LLM, exactly
/// like <c>Docket.WorkerHarness</c>. Where that harness proves one echo forward, this
/// one carries the several small roles the four multi-machine scenarios need, each
/// selected entirely by the task's opaque prose <b>description</b> (§7) so one
/// <c>default</c> profile drives a whole fleet off the wire.
///
/// <para>It speaks ACP on stdin/stdout. <c>docketd</c> injects the plane MCP
/// server on <c>session/new</c>; this process then calls <c>get_task</c> and
/// branches on the description:
/// <list type="bullet">
/// <item><c>handshake-serve</c> — mint a nonce, bind a loopback server that hands the
/// nonce to any caller, <c>register_service("handshake")</c>, write the nonce to an
/// atomic marker, and stay working (never reporting) until torn down.</item>
/// <item><c>handshake-consume</c> — <c>open_forward("handshake")</c>, read the nonce
/// across the relay, and <c>report_result("handshake:&lt;nonce&gt;")</c>.</item>
/// <item><c>compute-serve</c> — a deterministic <c>n → n+1</c> line service registered
/// as <c>compute</c>; stays working.</item>
/// <item><c>compute-test</c> — <c>open_forward("compute")</c>, send a few inputs, verify
/// each came back incremented through the relay, and <c>report_result("compute:pass")</c>.</item>
/// <item><c>map:&lt;seed&gt;</c> — a fan-out leaf: report the deterministic transform
/// <c>report_result("map:&lt;sha16(seed)&gt;")</c>. No forward, no service.</item>
/// <item><c>datastore-serve</c> — a trivial seeded line-protocol store registered as
/// <c>db</c> (a mock, never real Postgres — the scripted tier stays cheap); stays working.</item>
/// <item><c>db-query</c> — <c>open_forward("db")</c>, query the seeded key across the
/// relay, and <c>report_result("row:&lt;value&gt;")</c>.</item>
/// </list></para>
///
/// No shell, argv only (§10). PDEATHSIG + stdin-EOF liveness bound the serve modes'
/// otherwise-open lifetime, and any failure writes <c>./harness_error.txt</c> and
/// exits non-zero so a harnessing test sees a hard failure rather than a hang.
/// </summary>
public static class Program
{
    /// <summary>The single seeded row the mock datastore answers with — a constant the
    /// db-query scenario asserts against (a real store would hold rows; the scripted
    /// tier bakes one in to stay cheap and deterministic).</summary>
    public const string DatastoreSeedKey = "seed-row";
    public const string DatastoreSeedValue = "row-42-alpha";

    /// <summary>Service names the serve/consume pairs rendezvous on (§8.2 Team scope).</summary>
    private const string HandshakeService = "handshake";
    private const string ComputeService = "compute";
    private const string DatastoreService = "db";

    /// <summary>The deterministic fan-out transform (§7): SHA-256 of the seed, first 16
    /// hex chars (upper). Kept trivial and reproducible so the test computes the exact
    /// expected value without sharing code across the process boundary.</summary>
    public static string MapTransform(string seed) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..16];

    public static async Task<int> Main(string[] args)
    {
        var cwd = Directory.GetCurrentDirectory();
        using var cts = new CancellationTokenSource();

        if (ParentDeathSignal.ArmAndParentAlreadyDead())
            return 1;

        return await AcpStdio.RunAsync(
            (session, ct) => RunAssignmentAsync(session, cwd, ct),
            cts);
    }

    private static async Task<int> RunAssignmentAsync(
        AcpStdio.Session session, string cwd, CancellationToken ct)
    {
        var (url, authorization) = AcpStdio.RequireConnection(session);

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(url),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = authorization },
        });

        using var setupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        setupCts.CancelAfter(TimeSpan.FromSeconds(60));
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: setupCts.Token);

        var assignment = await client.CallToolAsync(
            "get_task", new Dictionary<string, object?>(), cancellationToken: setupCts.Token);
        if (assignment.IsError == true)
            throw new InvalidOperationException("get_task returned an error: " + TextOf(assignment));

        var assignmentJson = TextOf(assignment);
        await File.WriteAllTextAsync(Path.Combine(cwd, "get_task.json"), assignmentJson, ct);

        var description = (DescriptionOf(assignmentJson) ?? string.Empty).Trim();
        return description switch
        {
            "handshake-serve" => await RunHandshakeServeAsync(client, cwd, ct),
            "handshake-consume" => await RunHandshakeConsumeAsync(client, cwd, ct),
            "compute-serve" => await RunComputeServeAsync(client, cwd, ct),
            "compute-test" => await RunComputeTestAsync(client, cwd, ct),
            "datastore-serve" => await RunDatastoreServeAsync(client, cwd, ct),
            "db-query" => await RunDbQueryAsync(client, cwd, ct),
            _ when description.StartsWith("map:", StringComparison.Ordinal) =>
                await RunMapAsync(client, description["map:".Length..], ct),
            _ when description.StartsWith("echo:", StringComparison.Ordinal) =>
                await RunEchoAsync(client, description["echo:".Length..], ct),
            _ => throw new InvalidOperationException($"unrecognized collaborator description: '{description}'"),
        };
    }

    // ── Handshake: prove cross-machine bytes carry an unforgeable nonce ─────────

    /// <summary>
    /// <c>handshake-serve</c>: mint a nonce, bind a loopback server that hands it back on
    /// request, register the service, and stay working (never reporting) so the row
    /// survives and stays forwardable. The nonce is written atomically to
    /// <c>./handshake-nonce.txt</c> so the harnessing test can read exactly what this
    /// machine generated and assert the consumer received it byte-for-byte.
    ///
    /// <para><b>Client-first, deliberately.</b> The server waits for the consumer's
    /// request line before answering, exactly like the compute/datastore services. A
    /// server that spoke first and closed immediately raced the forward's mechanics: the
    /// producer docketd dials this service the instant it gets its open-forward command,
    /// so an unsolicited write+close could reach — and tear down — the producer end
    /// <em>before</em> the consumer end was paired at the relay, delivering the consumer
    /// an EOF instead of the nonce (a CI-only failure under slower scheduling). Requiring
    /// the request first proves the full bidirectional tunnel is established before any
    /// byte flows back, and keeps the connection open (no write-then-close).</para>
    /// </summary>
    private static Task<int> RunHandshakeServeAsync(McpClient client, string cwd, CancellationToken ct)
    {
        var nonce = Guid.NewGuid().ToString("N");
        return ServeAsync(
            client, cwd, HandshakeService,
            marker: ("handshake-nonce.txt", nonce),
            handleConnection: async (stream, connCt) =>
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                // Answer each request line with the nonce; loop until the peer closes.
                while (await reader.ReadLineAsync(connCt) is not null)
                    await writer.WriteLineAsync(nonce.AsMemory(), connCt);
            },
            ct);
    }

    /// <summary>
    /// <c>handshake-consume</c>: open the forward, send a request line, then read the
    /// nonce the server hands back, and report <c>handshake:&lt;nonce&gt;</c>. Speaking
    /// first (client-first, like compute/datastore) is what makes this reliable — see
    /// <see cref="RunHandshakeServeAsync"/>. The nonce it reports is the proof the bytes
    /// crossed the relay from the other machine intact.
    /// </summary>
    private static async Task<int> RunHandshakeConsumeAsync(McpClient client, string cwd, CancellationToken ct)
    {
        using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        opCts.CancelAfter(TimeSpan.FromSeconds(60));
        var opCt = opCts.Token;

        var (host, port) = await OpenForwardAsync(client, HandshakeService, opCt);
        string nonce;
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Parse(host), port, opCt);
            await using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            await writer.WriteLineAsync("nonce".AsMemory(), opCt); // request first: prove the tunnel before the server answers
            nonce = (await reader.ReadLineAsync(opCt))
                ?? throw new InvalidOperationException("handshake server closed before sending a nonce");
        }

        await ReportAsync(client, $"handshake:{nonce}", opCt);
        await WriteMarkerAtomicAsync(Path.Combine(cwd, "consume.json"),
            new JsonObject { ["service"] = HandshakeService, ["nonce"] = nonce }.ToJsonString());
        return 0;
    }

    // ── Compute: prove a request/response service round-trips over the relay ────

    /// <summary><c>compute-serve</c>: a deterministic <c>n → n+1</c> line service.</summary>
    private static Task<int> RunComputeServeAsync(McpClient client, string cwd, CancellationToken ct) =>
        ServeAsync(
            client, cwd, ComputeService, marker: null,
            handleConnection: async (stream, connCt) =>
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                string? line;
                while ((line = await reader.ReadLineAsync(connCt)) is not null)
                {
                    if (!int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                        continue; // ignore malformed input; a real service would error
                    await writer.WriteLineAsync((n + 1).ToString(CultureInfo.InvariantCulture).AsMemory(), connCt);
                }
            },
            ct);

    /// <summary>
    /// <c>compute-test</c>: open the forward and drive a few inputs through it, verifying
    /// each came back incremented across the relay. All pass → <c>compute:pass</c>; any
    /// mismatch throws, so the caller writes <c>harness_error.txt</c> and exits non-zero
    /// (reporting nothing) and the test sees a hard fail rather than a false success.
    /// </summary>
    private static async Task<int> RunComputeTestAsync(McpClient client, string cwd, CancellationToken ct)
    {
        using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        opCts.CancelAfter(TimeSpan.FromSeconds(60));
        var opCt = opCts.Token;

        var (host, port) = await OpenForwardAsync(client, ComputeService, opCt);
        int[] inputs = [1, 2, 41];
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Parse(host), port, opCt);
            await using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            foreach (var n in inputs)
            {
                await writer.WriteLineAsync(n.ToString(CultureInfo.InvariantCulture).AsMemory(), opCt);
                var reply = await reader.ReadLineAsync(opCt)
                    ?? throw new InvalidOperationException($"compute service closed before answering {n}");
                if (!int.TryParse(reply, NumberStyles.Integer, CultureInfo.InvariantCulture, out var got) || got != n + 1)
                    throw new InvalidOperationException($"compute mismatch: {n} → '{reply}', expected {n + 1}");
            }
        }

        await ReportAsync(client, "compute:pass", opCt);
        await WriteMarkerAtomicAsync(Path.Combine(cwd, "compute.json"),
            new JsonObject { ["service"] = ComputeService, ["checked"] = inputs.Length }.ToJsonString());
        return 0;
    }

    // ── Datastore: prove a seeded row is fetched across machines over the relay ─

    /// <summary><c>datastore-serve</c>: a mock line-protocol store — one seeded row,
    /// answered on a matching key line (never real Postgres; the scripted tier stays cheap).</summary>
    private static Task<int> RunDatastoreServeAsync(McpClient client, string cwd, CancellationToken ct) =>
        ServeAsync(
            client, cwd, DatastoreService, marker: null,
            handleConnection: async (stream, connCt) =>
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                string? key;
                while ((key = await reader.ReadLineAsync(connCt)) is not null)
                {
                    var value = key == DatastoreSeedKey ? DatastoreSeedValue : "MISS";
                    await writer.WriteLineAsync(value.AsMemory(), connCt);
                }
            },
            ct);

    /// <summary><c>db-query</c>: open the forward, query the seeded key across the relay,
    /// and report <c>row:&lt;value&gt;</c> — the value it reports proves the fetch reached
    /// the other machine's store.</summary>
    private static async Task<int> RunDbQueryAsync(McpClient client, string cwd, CancellationToken ct)
    {
        using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        opCts.CancelAfter(TimeSpan.FromSeconds(60));
        var opCt = opCts.Token;

        var (host, port) = await OpenForwardAsync(client, DatastoreService, opCt);
        string value;
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Parse(host), port, opCt);
            await using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            await writer.WriteLineAsync(DatastoreSeedKey.AsMemory(), opCt);
            value = await reader.ReadLineAsync(opCt)
                ?? throw new InvalidOperationException("datastore closed before answering the query");
        }

        await ReportAsync(client, $"row:{value}", opCt);
        await WriteMarkerAtomicAsync(Path.Combine(cwd, "query.json"),
            new JsonObject { ["service"] = DatastoreService, ["value"] = value }.ToJsonString());
        return 0;
    }

    // ── Fan-out leaf: a pure deterministic transform, no forward ────────────────

    /// <summary><c>map:&lt;seed&gt;</c>: report the deterministic transform of the seed and
    /// exit, driving working → verifying. The fan-out property is that a Lead can spread
    /// many of these across machines and collect the correct set back.</summary>
    /// <summary>
    /// <c>echo:&lt;text&gt;</c> — print <paramref name="text"/> to stderr, then report.
    /// ACP owns stdout (JSON-RPC); §12 capture tees stderr, so this is how the
    /// fleet produces a transcript with known content for the serving path.
    /// </summary>
    private static async Task<int> RunEchoAsync(McpClient client, string text, CancellationToken ct)
    {
        using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        opCts.CancelAfter(TimeSpan.FromSeconds(30));
        await Console.Error.WriteLineAsync(text);
        await Console.Error.FlushAsync(opCts.Token);
        await ReportAsync(client, $"echo:{text}", opCts.Token);
        return 0;
    }

    private static async Task<int> RunMapAsync(McpClient client, string seed, CancellationToken ct)
    {
        using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        opCts.CancelAfter(TimeSpan.FromSeconds(30));
        await ReportAsync(client, $"map:{MapTransform(seed)}", opCts.Token);
        return 0;
    }

    // ── Shared plumbing ─────────────────────────────────────────────────────────

    /// <summary>
    /// The serve-mode skeleton shared by every server role: bind a loopback listener
    /// BEFORE registering (§8.2 — advertise only a port you have bound), register the
    /// service, optionally drop an atomic diagnostics marker, then serve accepted
    /// connections until stdin-EOF / kill tears us down. It <b>never reports a result</b>:
    /// the task must stay <c>working</c> for the store to keep the service row.
    /// </summary>
    private static async Task<int> ServeAsync(
        McpClient client, string cwd, string serviceName,
        (string Name, string Content)? marker,
        Func<NetworkStream, CancellationToken, Task> handleConnection,
        CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serveLoop = AcceptLoopAsync(listener, handleConnection, ct);

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

            if (marker is { } m)
                await WriteMarkerAtomicAsync(Path.Combine(cwd, m.Name), m.Content);

            // A breadcrumb every serve mode leaves so a test can confirm the bound port.
            await WriteMarkerAtomicAsync(Path.Combine(cwd, "service.json"),
                new JsonObject { ["service"] = serviceName, ["port"] = boundPort }.ToJsonString());

            try { await serveLoop; }
            catch (OperationCanceledException) { /* stdin-EOF / kill tore us down */ }
            return 0;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task AcceptLoopAsync(
        TcpListener listener, Func<NetworkStream, CancellationToken, Task> handleConnection, CancellationToken ct)
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
            _ = HandleConnectionAsync(socket, handleConnection, ct);
        }
    }

    private static async Task HandleConnectionAsync(
        Socket socket, Func<NetworkStream, CancellationToken, Task> handleConnection, CancellationToken ct)
    {
        using (socket)
        await using (var stream = new NetworkStream(socket, ownsSocket: false))
        {
            try { await handleConnection(stream, ct); }
            catch (Exception) { /* peer closed / torn down */ }
        }
    }

    /// <summary>Issue <c>open_forward</c> and parse the loopback address it hands back (§8.3).</summary>
    private static async Task<(string Host, int Port)> OpenForwardAsync(
        McpClient client, string serviceName, CancellationToken ct)
    {
        var opened = await client.CallToolAsync(
            "open_forward",
            new Dictionary<string, object?> { ["serviceName"] = serviceName },
            cancellationToken: ct);
        if (opened.IsError == true)
            throw new InvalidOperationException("open_forward returned an error: " + TextOf(opened));

        using var doc = JsonDocument.Parse(TextOf(opened));
        var root = doc.RootElement;
        var host = root.GetProperty("host").GetString()
            ?? throw new InvalidOperationException("open_forward response missing host");
        return (host, root.GetProperty("port").GetInt32());
    }

    private static async Task ReportAsync(McpClient client, string reference, CancellationToken ct)
    {
        var reported = await client.CallToolAsync(
            "report_result",
            new Dictionary<string, object?> { ["resultReference"] = reference },
            cancellationToken: ct);
        if (reported.IsError == true)
            throw new InvalidOperationException("report_result returned an error: " + TextOf(reported));
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

    /// <summary>
    /// Writes a marker atomically (temp sibling + rename) so a harnessing test — which
    /// polls <c>File.Exists</c> then reads — sees either no file or the whole content,
    /// never a create-then-write gap. The §10 harness convention.
    /// </summary>
    private static async Task WriteMarkerAtomicAsync(string path, string content)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private static string TextOf(CallToolResult result)
    {
        if (result.StructuredContent is { } structured)
            return structured.GetRawText();
        return string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
    }
}
