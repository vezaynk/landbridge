using Docket.ControlPlane.Tests;
using Docket.Runner;

namespace Docket.MultiMachine.Tests;

/// <summary>
/// The per-CLI fixture the shared real-harness bar is parameterized by. Everything
/// harness-specific lives here — spawn argv, stdin, event mapping, MCP files, tool
/// spelling, whether the stream carries a cost — so a new harness is a new fixture
/// plus a Category-tagged wrapper, not another copy of the verifying/usage/resume
/// bodies. Characterization facts that invert across CLIs (dead-man hang, stop
/// delivery, permission) stay in the per-harness files.
/// </summary>
internal sealed class RealHarnessProfile
{
    public required string Name { get; init; }
    public required string Bin { get; init; }
    public required StdinPolicy Stdin { get; init; }
    public IReadOnlyDictionary<string, string>? EventMapping { get; init; }
    public IReadOnlyList<ProfileFile>? Files { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public required string GetTask { get; init; }
    public required string ReportResult { get; init; }
    public required string RequestInput { get; init; }
    public required UsageExpectation Usage { get; init; }
    public bool NamesModel { get; init; }
    public required bool SupportsResume { get; init; }
    public string FailureHypotheses { get; init; } = "";
    public string[] ParkSpawnExtra { get; init; } = [];
    public required Func<string, string, string[], string[]> Spawn { get; init; }
    public Func<string, string[]>? Resume { get; init; }
    public Func<FleetRig, IDisposable?>? Attach { get; init; }

    public string EchoTools => $"{GetTask},{ReportResult}";
    public string ParkTools => $"{GetTask},{ReportResult},{RequestInput}";

    public string McpToolsRule =>
        $" Docket's tools are MCP tools, named exactly {GetTask}, {ReportResult} and so " +
        "on — call them as tools, under those names. There is no `docket` program: no such " +
        "command exists on this machine, so never run `docket` in a shell, and never try to " +
        "reach the docket MCP server yourself over HTTP or with curl. (A shell command your " +
        "assignment explicitly asks for is a different thing, and is fine.) If a docket MCP " +
        "tool is missing or errors, report that with " + ReportResult + " instead of working around it.";

    public string EchoPrompt =>
        "You are a Docket worker agent. Your FIRST action must be to call the " +
        GetTask + " tool to read your assignment. The assignment's description tells " +
        "you the exact string to report. Your ONLY other action is to call the " +
        ReportResult + " tool once, with that exact string as resultReference. Do not " +
        "write files, do not explain, do not ask questions. Two tool calls total: " +
        GetTask + ", then " + ReportResult + "." + McpToolsRule;

    public string RememberThenAsk(string nonce) =>
        "You are a Docket worker agent. Remember this value for the rest of this conversation: " +
        nonce + ". Do not write it to any file, and do not put it in any tool call yet. Now call " +
        "the " + GetTask + " tool and do exactly what its description tells you." + McpToolsRule;

    public string ResumeAndReport =>
        "Your task has resumed. FIRST call the " + GetTask + " tool — it carries the answer " +
        "you were waiting for. Then call the " + ReportResult + " tool exactly once, with " +
        "resultReference set to the exact value you were asked to remember earlier in this " +
        "conversation, and nothing else. Two tool calls total." + McpToolsRule;

    public static string AskThenStop(string nonce) =>
        $"""
         Remember this value for the rest of this conversation: {nonce}. Do not write it
         to any file. Call request_input exactly once, with kind 'question' and question
         set to this exact text (no quotes, no other text):

         may I report the remembered value now?

         Then stop and end your turn. Do NOT call report_result on this turn. Do not create or
         edit files.
         """;

    public const string AskThenStopDescription =
        """
        Call request_input exactly once, with kind 'question' and question set to this exact
        text (no quotes, no other text):

        may I report the remembered value now?

        Then stop and end your turn. Do NOT call report_result on this turn. Do not create or
        edit files.
        """;

    public FleetRig OpenEchoRig(PostgresFixture pg) =>
        new(pg, Spawn(EchoPrompt, EchoTools, []),
            terminalEvents: true, eventMapping: EventMapping, stdin: Stdin, files: Files, env: Env);

    public FleetRig OpenParkRig(PostgresFixture pg, string nonce) =>
        new(
            pg,
            Spawn(RememberThenAsk(nonce), ParkTools, ParkSpawnExtra),
            terminalEvents: false, eventMapping: EventMapping, stdin: Stdin, files: Files, env: Env);

    public IDisposable? AttachTo(FleetRig rig) => Attach?.Invoke(rig);

    /// <summary>
    /// Pull a session id off one captured stdout line using this profile's
    /// <c>events.mapping</c> (or the claude defaults). The bar uses this as the
    /// harness-side proof of a resume: two instances reporting the same id cannot
    /// be a cold start.
    /// </summary>
    public string? SessionIdFromLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            var typeKey = Map("type_key", "type");
            var systemType = Map("system_type", "system");
            var subtypeKey = Map("subtype_key", "subtype");
            var initSubtype = Map("init_subtype", "init");
            var sessionKey = Map("session_id_key", "session_id");
            if (Text(root, typeKey) != systemType) return null;
            if (Text(root, subtypeKey) != initSubtype) return null;
            return Text(root, sessionKey);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        string Map(string key, string fallback) =>
            EventMapping is not null && EventMapping.TryGetValue(key, out var v) && v.Length > 0
                ? v
                : fallback;

        static string? Text(System.Text.Json.JsonElement o, string key) =>
            o.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString()
                : null;
    }
}

internal enum UsageExpectation
{
    /// <summary>The stream carries no usage the bar can assert on.</summary>
    None,
    /// <summary>Tokens land, but the harness computes no cost (Codex).</summary>
    Tokens,
    /// <summary>The harness self-reports a positive USD cost (Claude, OpenCode, Grok).</summary>
    Cost,
}
