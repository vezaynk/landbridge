using System.Text.Json;

namespace Landbridge.HarnessSupport;

/// <summary>
/// A scripted reference harness as an <b>Agent Client Protocol agent</b> — the other side of
/// <c>Landbridge.Runner</c>'s <c>AcpClient</c>, and the reason the whole zero-cost E2E tier
/// exercises the ACP path instead of only the token-spending one.
///
/// <para><b>Why this exists rather than a mock.</b> <c>AcpClientTests</c> already drives
/// landbridged's half against a scripted peer, but both halves of that test are the same
/// author's reading of the spec, so they agree about anything they both get wrong. Here the
/// client is the real <c>AcpClient</c>, spawned by the real <c>ProcessSupervisor</c> from a
/// real <c>protocol: acp</c> profile, and the agent is a real process that authenticates
/// back to the real plane over MCP. What is fake is the model, and only the model.</para>
///
/// <para><b>What it implements, and why exactly this much.</b> <c>initialize</c>,
/// <c>session/new</c>, <c>session/prompt</c> and <c>session/cancel</c> — the four methods
/// landbridged actually sends. It declares <c>loadSession</c> and <c>mcpCapabilities.http</c>
/// true, matching what every real agent measured on 2026-08-15 declares, so the capability
/// branches landbridged takes here are the ones it takes in production.</para>
///
/// <para><b>Shared, not copied.</b> Both reference harnesses link this one source, the same
/// way they link <c>ParentDeathSignal</c>: the protocol half is identical for both, and two
/// divergent copies of a JSON-RPC server in the test tree would be a second thing to keep
/// right. Each harness supplies only its own <c>work</c> — connect with these credentials,
/// do the scripted thing, return an exit code.</para>
///
/// <para><b>The MCP wiring is the point of the exercise.</b> A stream-mode harness reads
/// <c>--mcp-config</c> off its argv; here the plane's server arrives as a <c>session/new</c>
/// parameter, bearer header and all, and this agent uses it. That path — plane mints token →
/// landbridged translates the generated config → <c>mcpServers</c> on the wire → agent
/// authenticates with it — is untested anywhere else without spending money.</para>
///
/// <para><b>stdin belongs to the protocol.</b> Unlike the stream-mode harness, this one must
/// not watch stdin for EOF as a liveness signal: stdin <em>is</em> the request channel, and
/// consuming it would eat landbridged's requests. EOF still ends the agent — it falls out of the
/// read loop — which is the same dead-man's switch by a different route, and is exactly what
/// ACP's own shutdown rule ("the client terminates the subprocess after closing stdin")
/// prescribes.</para>
/// </summary>
internal static class AcpAgentLoop
{
    /// <summary>
    /// Serves one ACP conversation on stdin/stdout until the stream ends. Returns 0 on a
    /// clean EOF; the scripted work's own failures are written to
    /// <c>./harness_error.txt</c> exactly as in stream mode, so a test sees the same
    /// artifact either way.
    /// </summary>
    public static async Task<int> RunAsync(
        string cwd, Func<string, string, string, CancellationToken, Task<int>> work, CancellationToken ct,
        string sessionId = "harness-session-1")
    {
        var stdout = Console.Out;
        string? url = null;
        string? authorization = null;
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        string? line;
        while ((line = await Console.In.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                var method = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString()
                    : null;
                var hasId = root.TryGetProperty("id", out var id) && id.ValueKind is not JsonValueKind.Null;

                switch (method)
                {
                    case "initialize" when hasId:
                        await RespondAsync(stdout, id, w =>
                        {
                            // Version 1, because that is what every shipping agent answers
                            // and therefore the branch landbridged takes in production.
                            w.WriteNumber("protocolVersion", 1);
                            w.WriteStartObject("agentCapabilities");
                            w.WriteBoolean("loadSession", true);
                            w.WriteStartObject("mcpCapabilities");
                            w.WriteBoolean("http", true);
                            w.WriteEndObject();
                            w.WriteEndObject();
                            w.WriteStartObject("agentInfo");
                            w.WriteString("name", "landbridge-worker-harness");
                            w.WriteString("version", "1");
                            w.WriteEndObject();
                        }).ConfigureAwait(false);
                        break;

                    // Both session openers take the same parameters and, for this agent,
                    // mean the same thing: here is your cwd and your MCP server. `load`
                    // additionally proves landbridged took the resume branch, which the test
                    // asserts by the method name alone.
                    case "session/new" when hasId:
                    case "session/load" when hasId:
                        (url, authorization) = ReadLandbridgeServer(root);
                        if (method == "session/load"
                            && root.TryGetProperty("params", out var lp)
                            && lp.TryGetProperty("sessionId", out var given)
                            && given.ValueKind == JsonValueKind.String)
                            sessionId = given.GetString()!;

                        // The work dir is a session parameter under ACP, and the scripted
                        // work writes its artifacts there, so honour it rather than assuming
                        // the spawn cwd — they are the same today and the assumption is the
                        // kind that rots.
                        if (root.TryGetProperty("params", out var np)
                            && np.TryGetProperty("cwd", out var c)
                            && c.ValueKind == JsonValueKind.String
                            && c.GetString() is { Length: > 0 } dir)
                            cwd = dir;

                        var opened = sessionId;

                        // Test seam: record HOW the session opened. Under stream mode a
                        // resume was observable in the respawned argv, which a harness could
                        // simply echo; here it is a method name on a connection nobody else
                        // sees, so the harness has to say. §11's whole claim — that a resume
                        // is a resume and not a quiet cold start — is unassertable otherwise.
                        try
                        {
                            await File.WriteAllTextAsync(
                                Path.Combine(cwd, "acp_session.json"),
                                $$"""{"method":"{{method}}","session_id":"{{opened}}","cwd":{{JsonSerializer.Serialize(cwd)}}}""",
                                ct).ConfigureAwait(false);
                        }
                        catch { /* best effort, exactly like the other artifacts */ }

                        await RespondAsync(stdout, id, w => w.WriteString("sessionId", opened)).ConfigureAwait(false);
                        break;

                    case "session/prompt" when hasId:
                        await RunTurnAsync(stdout, id, sessionId, url, authorization, cwd, work, turnCts.Token)
                            .ConfigureAwait(false);
                        break;

                    case "session/cancel":
                        // A notification: no reply, and the turn in flight is what it ends.
                        await turnCts.CancelAsync().ConfigureAwait(false);
                        break;

                    default:
                        if (hasId)
                            await ErrorAsync(stdout, id, -32601, $"harness does not implement '{method}'")
                                .ConfigureAwait(false);
                        break;
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// One prompt turn: connect to the plane with the credentials that arrived on
    /// <c>session/new</c>, run the same scripted work the stream-mode harness runs, and
    /// answer with a stop reason.
    ///
    /// <para>Tool calls are announced as <c>session/update</c> notifications so the turn
    /// moves landbridged's progress clock — under ACP that is the <em>only</em> source of
    /// <c>tool-call</c> events, so without them a long turn would look wedged to the plane
    /// exactly as a mismapped stream profile does.</para>
    /// </summary>
    private static async Task RunTurnAsync(
        TextWriter stdout, JsonElement id, string sessionId,
        string? url, string? authorization, string cwd,
        Func<string, string, string, CancellationToken, Task<int>> work, CancellationToken ct)
    {
        if (url is null || authorization is null)
        {
            await ErrorAsync(stdout, id, -32603, "no MCP server was supplied on session/new").ConfigureAwait(false);
            return;
        }

        try
        {
            await ToolCallAsync(stdout, sessionId, "call-work", "Doing the assigned work").ConfigureAwait(false);
            var exit = await work(url, authorization, cwd, ct).ConfigureAwait(false);

            await RespondAsync(stdout, id, w =>
                w.WriteString("stopReason", exit == 0 ? "end_turn" : "refusal")).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The spec's own word for a turn ended by session/cancel, and what landbridged's
            // stop path expects to see.
            await RespondAsync(stdout, id, w => w.WriteString("stopReason", "cancelled")).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            try
            {
                await File.WriteAllTextAsync(
                    Path.Combine(cwd, "harness_error.txt"), e.ToString(), CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* best effort, exactly as in stream mode */ }
            await ErrorAsync(stdout, id, -32603, e.Message).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Pulls the plane's URL and bearer out of ACP's <c>mcpServers</c> array — the
    /// shape landbridged builds from the dispatch token and URL. Deliberately reads
    /// it the long way rather than trusting position: this is the assertion that
    /// landbridged sent something an agent can actually use.
    /// </summary>
    private static (string? Url, string? Authorization) ReadLandbridgeServer(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var p) || !p.TryGetProperty("mcpServers", out var servers)
            || servers.ValueKind != JsonValueKind.Array)
            return (null, null);

        foreach (var server in servers.EnumerateArray())
        {
            if (server.ValueKind != JsonValueKind.Object)
                continue;
            if (!server.TryGetProperty("url", out var u) || u.ValueKind != JsonValueKind.String)
                continue;

            string? auth = null;
            if (server.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Array)
                foreach (var header in headers.EnumerateArray())
                    if (header.ValueKind == JsonValueKind.Object
                        && header.TryGetProperty("name", out var hn) && hn.GetString() == "Authorization"
                        && header.TryGetProperty("value", out var hv) && hv.ValueKind == JsonValueKind.String)
                        auth = hv.GetString();

            return (u.GetString(), auth);
        }

        return (null, null);
    }

    private static Task ToolCallAsync(TextWriter stdout, string sessionId, string callId, string title) =>
        WriteAsync(stdout, w =>
        {
            w.WriteString("method", "session/update");
            w.WriteStartObject("params");
            w.WriteString("sessionId", sessionId);
            w.WriteStartObject("update");
            w.WriteString("sessionUpdate", "tool_call");
            w.WriteString("toolCallId", callId);
            w.WriteString("title", title);
            w.WriteString("kind", "other");
            w.WriteString("status", "pending");
            w.WriteEndObject();
            w.WriteEndObject();
        });

    private static Task RespondAsync(TextWriter stdout, JsonElement id, Action<Utf8JsonWriter> writeResult) =>
        WriteAsync(stdout, w =>
        {
            w.WritePropertyName("id");
            id.WriteTo(w);
            w.WriteStartObject("result");
            writeResult(w);
            w.WriteEndObject();
        });

    private static Task ErrorAsync(TextWriter stdout, JsonElement id, int code, string message) =>
        WriteAsync(stdout, w =>
        {
            w.WritePropertyName("id");
            id.WriteTo(w);
            w.WriteStartObject("error");
            w.WriteNumber("code", code);
            w.WriteString("message", message);
            w.WriteEndObject();
        });

    /// <summary>
    /// One newline-delimited JSON-RPC message. Serialized because a turn's notifications and
    /// its eventual response can race, and the transport forbids embedded newlines — two
    /// interleaved writers would produce a line that parses as neither.
    /// </summary>
    private static async Task WriteAsync(TextWriter stdout, Action<Utf8JsonWriter> writeBody)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            writeBody(w);
            w.WriteEndObject();
        }

        var text = System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
        await WriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await stdout.WriteAsync(text).ConfigureAwait(false);
            await stdout.WriteAsync('\n').ConfigureAwait(false);
            await stdout.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    private static readonly SemaphoreSlim WriteLock = new(1, 1);
}
