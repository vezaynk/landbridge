using Landbridge.ControlPlane.Tests;
using Landbridge.Runner;

namespace Landbridge.MultiMachine.Tests;

/// <summary>
/// The per-CLI fixture the shared real-harness bar is parameterized by. What is left of
/// "harness-specific" after the ACP migration is mostly naming: the entry point, how this
/// vendor spells landbridge's MCP tools, and whether its usage carries a cost. Spawn argv,
/// stdin policy and event mappings used to live here too — three vendors' worth of them —
/// and the protocol took all three.
/// </summary>
internal sealed class RealHarnessProfile
{
    public required string Name { get; init; }
    public required string Bin { get; init; }
    public IReadOnlyList<ProfileFile>? Files { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public required string GetTask { get; init; }
    public required string ReportResult { get; init; }
    public required string RequestInput { get; init; }
    public required UsageExpectation Usage { get; init; }
    public bool NamesModel { get; init; }
    public required bool SupportsResume { get; init; }
    public string FailureHypotheses { get; init; } = "";

    /// <summary>
    /// The ACP entry point: the argv that starts this harness as an Agent Client Protocol
    /// agent over stdio. Natively a subcommand for OpenCode, Grok and Goose, an adapter
    /// binary for Claude and Codex. Carries no prompt — an ACP agent takes none on argv.
    /// </summary>
    public required string[] AcpSpawn { get; init; }
    /// <summary>
    /// ACP <c>authenticate</c> method id, when the agent refuses <c>session/new</c>.
    /// Codex offers <c>api-key</c> and <c>chat-gpt</c>; only the first works unattended.
    /// </summary>
    public string? AuthMethod { get; init; }
    /// <summary>
    /// ACP <c>session/set_config_option</c> pins, when the agent advertises them.
    /// OpenCode ACP otherwise sits on <c>opencode/big-pickle</c> and never turns.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ConfigOptions { get; init; }
    /// <summary>
    /// ACP <c>session/set_mode</c> pin, when the agent advertised that mode.
    /// Goose defaults to <c>auto</c>; Landbridge pins <c>approve</c>.
    /// </summary>
    public string? SessionMode { get; init; }
    public Func<FleetRig, IDisposable?>? Attach { get; init; }

    public string EchoTools => $"{GetTask},{ReportResult}";
    public string ParkTools => $"{GetTask},{ReportResult},{RequestInput}";

    public string McpToolsRule =>
        $" Landbridge's tools are MCP tools, named exactly {GetTask}, {ReportResult} and so " +
        "on — call them as tools, under those names. There is no `landbridge` program: no such " +
        "command exists on this machine, so never run `landbridge` in a shell, and never try to " +
        "reach the landbridge MCP server yourself over HTTP or with curl. (A shell command your " +
        "assignment explicitly asks for is a different thing, and is fine.) If a landbridge MCP " +
        "tool is missing or errors, report that with " + ReportResult + " instead of working around it.";

    public string EchoPrompt =>
        "You are a Landbridge worker agent. Your FIRST action must be to call the " +
        GetTask + " tool to read your assignment. The assignment's description tells " +
        "you the exact string to report. Your ONLY other action is to call the " +
        ReportResult + " tool once, with that exact string as resultReference. Do not " +
        "write files, do not explain, do not ask questions. Two tool calls total: " +
        GetTask + ", then " + ReportResult + "." + McpToolsRule;

    /// <summary>
    /// Park first-leg opening turn. Same closed two-tool shape as <see cref="EchoPrompt"/>:
    /// get_inbox, then request_input, do not explain. "Do exactly what its description
    /// tells you" made grok 1.0.3 recite the inbox and <c>end_turn</c> (#222).
    /// </summary>
    public string RememberThenAsk(string nonce) =>
        "You are a Landbridge worker agent. Remember this test nonce for the rest of this conversation: " +
        nonce + ". Do not write it to any file, and do not put it in any tool call yet. Your FIRST " +
        "action must be to call the " + GetTask + " tool to read your assignment. Your ONLY other " +
        "action is to call the " + RequestInput + " tool exactly once as the assignment describes, " +
        "then stop. Do not call " + ReportResult + ". Do not write files, do not explain, do not " +
        "quote the assignment. Two tool calls total: " + GetTask + ", then " + RequestInput + "." +
        McpToolsRule;

    /// <summary>
    /// Wake after <c>session/load</c>. Must work for two different redispatches:
    /// a first-leg retry that never asked (no answer on get_session — ask, then stop)
    /// and a park/resume (answer is there — report the nonce). Sending only
    /// "report now" made OpenCode's silent first turn skip the ask on retry.
    /// Same closed two-tool shape as <see cref="EchoPrompt"/> / <see cref="RememberThenAsk"/>:
    /// grok 1.0.3 recited the inbox and <c>end_turn</c>ed (#222).
    /// </summary>
    public string ResumeAndReport =>
        "Your session resumed. Your FIRST action must be to call the " + GetTask +
        " tool to read your assignment. Do not explain. " +
        "If the assignment has no answer yet, your ONLY other action is to call the " +
        RequestInput + " tool exactly once as the assignment describes, then stop. Do not call " +
        ReportResult + ". Two tool calls total: " + GetTask + ", then " + RequestInput + ". " +
        "If the assignment already has an answer, your ONLY other action is to call the " +
        ReportResult + " tool exactly once, with resultReference set to the exact nonce you were " +
        "asked to remember, and nothing else. Two tool calls total: " + GetTask + ", then " +
        ReportResult + "." + McpToolsRule;

    public string AskThenStopDescription =>
        "Call " + RequestInput + " exactly once, with kind 'question' and question set to this exact " +
        "text (no quotes, no other text):\n\n" +
        "may I report the remembered value now?\n\n" +
        "Then stop and end your turn. Do NOT call " + ReportResult + " on this turn. Do not create or " +
        "edit files.";

    /// <summary>
    /// The wake-up turn for a live ACP session. Says only "read your assignment" — the
    /// answer itself is pulled over the authenticated MCP call, and that pull is the read
    /// receipt (§11). Names the tools the way this harness spells them.
    /// </summary>
    public string FollowUpTurn =>
        "There is new input on your assignment. Call the " + GetTask + " tool to read it, " +
        "then continue." + McpToolsRule;

    /// <summary>
    /// The echo rig, on ACP. Note what is gone against the stream construction it replaces:
    /// no <c>events.mapping</c> (the shapes are in the spec), no <c>stdin</c> policy (stdin
    /// is the request channel and <c>closed</c> is refused), and no <c>files</c> (the plane's
    /// MCP server rides <c>session/new</c> instead of a config file with a live token in it).
    /// <c>env</c> stays: it configures the process, not the protocol.
    /// </summary>
    public FleetRig OpenEchoRig(PostgresFixture pg) =>
        new(pg, AcpSpawn, env: Env,
            prompt: EchoPrompt, followUp: FollowUpTurn, authMethod: AuthMethod,
            configOptions: ConfigOptions, sessionMode: SessionMode);

    /// <summary>
    /// The park/resume rig, on ACP. There is no <c>resume.args</c> here and that is the
    /// point: a resumed dispatch takes <c>session/load</c> on the connection landbridged opens,
    /// gated on the agent's <c>loadSession</c> capability — which every agent measured on
    /// 2026-08-15 declares true.
    /// </summary>
    public FleetRig OpenParkRig(PostgresFixture pg, string nonce)
    {
        if (!SupportsResume)
            throw new InvalidOperationException(Name + " does not support resume — the park bar must skip.");
        return new FleetRig(
            pg, AcpSpawn, env: Env,
            prompt: RememberThenAsk(nonce), followUp: ResumeAndReport, authMethod: AuthMethod,
            configOptions: ConfigOptions, sessionMode: SessionMode);
    }

    public IDisposable? AttachTo(FleetRig rig) => Attach?.Invoke(rig);

    /// <summary>
    /// Harness-side proof of resume: the id <c>session/new</c> returned, else the
    /// first <c>session/update</c> id (for <c>session/load</c>, which often echoes none).
    /// Skip initialize (<c>protocolVersion</c>); a later result with a sessionId is
    /// not the handshake — AcpClient stamps the first <c>session/new</c> result.
    /// </summary>
    public string? SessionIdFromTranscript(IEnumerable<string> lines)
    {
        string? fromNew = null;
        string? fromUpdate = null;
        foreach (var line in lines)
        {
            if (fromNew is null && JsonRpcSessionNewResultId(line) is { Length: > 0 } id)
                fromNew = id;
            fromUpdate ??= JsonRpcUpdateSessionId(line);
        }
        return fromNew ?? fromUpdate;
    }

    public string? SessionIdFromLine(string line) =>
        JsonRpcSessionNewResultId(line) ?? JsonRpcUpdateSessionId(line);

    /// <summary>
    /// <c>session/new</c> (and a load that echoes an id). Skip initialize, which
    /// is the only result that carries <c>protocolVersion</c>.
    /// </summary>
    private static string? JsonRpcSessionNewResultId(string line)
    {
        if (!TryParseRpc(line, out var root))
            return null;
        if (!root.TryGetProperty("result", out var result)
            || result.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;
        if (result.TryGetProperty("protocolVersion", out _))
            return null;
        if (result.TryGetProperty("sessionId", out var acpId)
            && acpId.ValueKind == System.Text.Json.JsonValueKind.String)
            return acpId.GetString();
        return null;
    }

    private static string? JsonRpcUpdateSessionId(string line)
    {
        if (!TryParseRpc(line, out var root))
            return null;
        if (root.TryGetProperty("method", out var method)
            && method.ValueKind == System.Text.Json.JsonValueKind.String
            && method.GetString() == "session/update"
            && root.TryGetProperty("params", out var p)
            && p.ValueKind == System.Text.Json.JsonValueKind.Object
            && p.TryGetProperty("sessionId", out var updateId)
            && updateId.ValueKind == System.Text.Json.JsonValueKind.String)
            return updateId.GetString();
        return null;
    }

    private static bool TryParseRpc(string line, out System.Text.Json.JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(line))
            return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return false;
            if (!doc.RootElement.TryGetProperty("jsonrpc", out _))
                return false;
            root = doc.RootElement.Clone();
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}

internal enum UsageExpectation
{
    /// <summary>The agent reports no usage the bar can assert on.</summary>
    None,
    /// <summary>Tokens land, but the agent computes no cost (Codex, OpenCode).</summary>
    Tokens,
    /// <summary>The agent self-reports a positive USD cost (Claude, Grok).</summary>
    Cost,
}
