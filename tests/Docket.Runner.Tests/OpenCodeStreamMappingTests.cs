using Docket.Contracts;
using Docket.Core;
using Microsoft.Extensions.Time.Testing;

namespace Docket.Runner.Tests;

/// <summary>
/// §10 event relay against a <b>third real harness</b>: what <see cref="EventsSource.Terminal"/>
/// reads out of OpenCode's <c>opencode run --format json</c> stream. Same shape of exercise as
/// <see cref="CodexStreamMappingTests"/> — the fixtures are the harness's shapes and the
/// assertions are about <em>Docket's</em> parser, so the findings hold with or without the
/// <c>opencode</c> binary.
///
/// <para><b>Provenance of the fixtures.</b> Derived from OpenCode's source at tag
/// <c>v1.18.17</c>, not captured from a run — no <c>opencode</c> binary was installed. Two files
/// fix every byte below. The envelope is <c>packages/opencode/src/cli/cmd/run.ts:678-691</c>,
/// whose <c>emit</c> writes <c>JSON.stringify({ type, timestamp, sessionID, ...data }) + EOL</c>
/// and is called with exactly six type strings (<c>tool_use</c>, <c>step_start</c>,
/// <c>step_finish</c>, <c>text</c>, <c>reasoning</c>, <c>error</c>). The parts are
/// <c>packages/schema/src/v1/session.ts</c> — <c>ToolPart</c> at <c>:315-322</c>,
/// <c>StepStartPart</c> at <c>:233-237</c>, <c>StepFinishPart</c> at <c>:240-256</c>. So these
/// are source-derived rather than doc-derived, which is stronger than the Codex fixtures but
/// still not a capture: confirming the wire against the real binary is
/// <c>RealOpenCodeCollaborationTests</c>' fifth fact, which exists for precisely this gap.</para>
///
/// <para><b>What these facts establish, in order.</b> (1) The session ref and the tool-call
/// progress signal are both reachable with the seams that already existed — the Codex trick for
/// the ref, flat mode for the calls. (2) The <b>usage</b> report was <em>not</em>: OpenCode nests
/// its counters where a bare property name cannot reach them, which is what made the usage keys
/// dotted paths (#144). The negative half of that is pinned here as its own fact, because "the
/// old scheme read nothing" is the claim that justifies the change. (3) OpenCode's reasoning
/// count is disjoint from its output where claude's and Codex's is a subset, so the reader folds
/// it back rather than letting the plane's total silently omit it.</para>
/// </summary>
public sealed class OpenCodeStreamMappingTests
{
    private static async Task<Result> RunAsync(
        string ndjson, TaskId task, IReadOnlyDictionary<string, string>? mapping = null)
    {
        var ring = new OutboundEventRing(capacity: 256);
        string? capturedSession = null;
        var warnings = new List<string>();

        var reader = new TerminalEventReader(
            task,
            ring,
            mapping ?? new Dictionary<string, string>(),
            new FakeTimeProvider(),
            onSessionId: id => capturedSession = id,
            warn: warnings.Add);

        using (var sr = new StringReader(ndjson))
            await reader.ReadToEndAsync(sr, CancellationToken.None);

        ring.Complete();
        var events = new List<RunnerEvent>();
        await foreach (var item in ring.ReadAllAsync(CancellationToken.None))
            events.Add(item.Event);

        return new Result(events, capturedSession, warnings);
    }

    private sealed record Result(List<RunnerEvent> Events, string? SessionId, List<string> Warnings);

    private static List<string> ToolNames(IEnumerable<RunnerEvent> events) =>
        events.OfType<ToolCallEvent>().Select(e => e.Tool).ToList();

    private static List<UsageReportedEvent> Usage(IEnumerable<RunnerEvent> events) =>
        events.OfType<UsageReportedEvent>().ToList();

    // ── The stream ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Earliest line of a run and therefore the profile's session-ref carrier. It has no
    /// <c>subtype</c> and no dedicated init type — <c>sessionID</c> is simply top-level on
    /// <em>every</em> line (<c>run.ts:682-685</c>), which is what makes the Codex trick work here
    /// too: point the sub-discriminator back at <c>type</c> and let the id key do the work.
    /// </summary>
    private const string StepStartLine = """
        {"type":"step_start","timestamp":1786000000000,"sessionID":"ses_7f3ad2c1","part":{"id":"prt_0001","sessionID":"ses_7f3ad2c1","messageID":"msg_0001","type":"step-start"}}
        """;

    /// <summary>
    /// One tool call, flattened: the name is at <c>part.tool</c> for every tool kind, so unlike
    /// Codex this needs no comma-separated alternatives. Note the MCP tool name — OpenCode spells
    /// it <c>docket_get_task</c>, not <c>mcp__docket__get_task</c>
    /// (<c>packages/opencode/src/mcp/catalog.ts:119</c>).
    /// </summary>
    private const string ToolUseLine = """
        {"type":"tool_use","timestamp":1786000001000,"sessionID":"ses_7f3ad2c1","part":{"id":"prt_0002","sessionID":"ses_7f3ad2c1","messageID":"msg_0001","type":"tool","callID":"call_01","tool":"docket_get_task","state":{"status":"completed","input":{},"output":"{}","title":"docket_get_task","metadata":{},"time":{"start":1786000000500,"end":1786000000900}}}}
        """;

    private const string BashToolUseLine = """
        {"type":"tool_use","timestamp":1786000002000,"sessionID":"ses_7f3ad2c1","part":{"id":"prt_0003","sessionID":"ses_7f3ad2c1","messageID":"msg_0001","type":"tool","callID":"call_02","tool":"bash","state":{"status":"completed","input":{},"output":"ok","title":"bash","metadata":{},"time":{"start":1786000001500,"end":1786000001900}}}}
        """;

    /// <summary>
    /// The usage-bearing line, and the whole reason this file exists. Three separate places where
    /// a bare property name cannot reach: the counters are under <c>part.tokens</c> (two levels
    /// down), the cache buckets are under <c>tokens.cache</c> (three), and <c>cost</c> sits
    /// <em>beside</em> the counters object at <c>part.cost</c> rather than at the line root.
    /// </summary>
    private const string StepFinishLine = """
        {"type":"step_finish","timestamp":1786000003000,"sessionID":"ses_7f3ad2c1","part":{"id":"prt_0004","sessionID":"ses_7f3ad2c1","messageID":"msg_0001","type":"step-finish","reason":"tool-calls","cost":0.0123,"tokens":{"total":1334,"input":900,"output":120,"reasoning":80,"cache":{"read":200,"write":34}}}}
        """;

    /// <summary>A second step on the same run. Its figures are that step's own, not a running
    /// total (<c>processor.ts:454</c> hands the part <c>usage.cost</c> while only the
    /// <em>message</em> accumulates at <c>:444</c>) — which is why the profile declares
    /// <c>usage_is_cumulative: false</c>.</summary>
    private const string SecondStepFinishLine = """
        {"type":"step_finish","timestamp":1786000004000,"sessionID":"ses_7f3ad2c1","part":{"id":"prt_0005","sessionID":"ses_7f3ad2c1","messageID":"msg_0001","type":"step-finish","reason":"stop","cost":0.0077,"tokens":{"total":700,"input":500,"output":60,"reasoning":40,"cache":{"read":100,"write":0}}}}
        """;

    /// <summary>The mapping the worked profile in <c>runner-config.md</c> declares. Kept in one
    /// place so every fact below exercises the operator-facing article rather than a
    /// test-only variant of it.</summary>
    private static Dictionary<string, string> OpenCodeMapping() => new(StringComparer.Ordinal)
    {
        // §11 resume ref: no init line exists, so both checks read `type` and the id key works.
        ["system_type"] = "step_start",
        ["subtype_key"] = "type",
        ["init_subtype"] = "step_start",
        ["session_id_key"] = "sessionID",
        // Progress clock: one `tool_use` line IS one tool call.
        ["tool_event_type"] = "tool_use",
        ["tool_name_path"] = "part.tool",
        // Usage: every one of these is a dotted path (#144).
        ["usage_type"] = "step_finish",
        ["usage_key"] = "part.tokens",
        ["usage_input_key"] = "input",
        ["usage_output_key"] = "output",
        ["usage_cache_read_key"] = "cache.read",
        ["usage_cache_write_key"] = "cache.write",
        ["usage_reasoning_key"] = "reasoning",
        ["usage_cost_key"] = "part.cost",
        // OpenCode reports no per-model breakdown at all.
        ["usage_models_key"] = "",
        // OpenCode already subtracts cache out of input (session.ts:366) — disjoint like claude.
        ["usage_cached_is_subset"] = "false",
        // ...but it ALSO subtracts reasoning out of output (session.ts:373), unlike either.
        ["usage_reasoning_is_subset"] = "false",
        // step_finish carries one step's own figures, not a running total.
        ["usage_is_cumulative"] = "false",
    };

    // ── The seams that already existed ──────────────────────────────────────────

    [Fact]
    public async Task The_session_ref_is_reachable_by_config_alone_via_the_step_start_line()
    {
        var task = TaskId.New();

        var result = await RunAsync(StepStartLine + "\n" + ToolUseLine, task, OpenCodeMapping());

        // The reader hands the ref to its callback and emits no event of its own — the supervisor
        // is what turns this into a SessionStartedEvent for the plane (see the ctor's docs).
        Assert.Equal("ses_7f3ad2c1", result.SessionId);
    }

    [Fact]
    public async Task Tool_calls_come_off_the_flat_tool_use_line_with_one_name_path()
    {
        var task = TaskId.New();

        var result = await RunAsync(
            string.Join("\n", StepStartLine, ToolUseLine, BashToolUseLine), task, OpenCodeMapping());

        // The MCP tool keeps OpenCode's own spelling: docketd reports what the harness said, and
        // the harness says `docket_get_task`.
        Assert.Equal(["docket_get_task", "bash"], ToolNames(result.Events));
    }

    /// <summary>
    /// The claude defaults find nothing in this stream — the same starting point Codex had. Worth
    /// pinning because it is what makes <c>events.mapping</c> mandatory for this harness rather
    /// than an optimisation, and because the operator-facing consequence is a one-line
    /// degradation notice rather than silence.
    /// </summary>
    [Fact]
    public async Task Without_a_mapping_the_claude_defaults_read_nothing_and_docketd_says_so()
    {
        var task = TaskId.New();

        var result = await RunAsync(
            string.Join("\n", StepStartLine, ToolUseLine, StepFinishLine), task);

        Assert.Null(result.SessionId);
        Assert.Empty(ToolNames(result.Events));
        Assert.Empty(Usage(result.Events));
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("no session ref and no tool-call", warning);
    }

    // ── The usage fit: why the keys became paths (#144) ─────────────────────────

    /// <summary>
    /// <b>The negative half of the #144 case.</b> Everything except the usage keys is mapped
    /// correctly, and the usage keys are spelled the only way the pre-#144 scheme allowed — bare
    /// property names, as deep as a bare name can go. The result is no usage at all, because
    /// <c>tokens</c> is not a property of the line root. A profile in this state looks configured,
    /// resumes correctly, has a working progress clock, and reports zero spend forever.
    /// </summary>
    [Fact]
    public async Task Bare_property_names_cannot_reach_OpenCodes_nested_counters()
    {
        var task = TaskId.New();
        var flat = OpenCodeMapping();
        flat["usage_key"] = "tokens";        // the counters are at part.tokens, not tokens
        flat["usage_cache_read_key"] = "read";  // ...and the cache buckets one deeper still
        flat["usage_cache_write_key"] = "write";
        flat["usage_cost_key"] = "cost";     // ...and cost is at part.cost, not cost

        var result = await RunAsync(StepStartLine + "\n" + StepFinishLine, task, flat);

        Assert.Empty(Usage(result.Events));
    }

    /// <summary>
    /// <b>The positive half.</b> The same three shapes, reached by paths, with no new config key
    /// invented for any of them: a container path (<c>part.tokens</c>), a bucket path one level
    /// deeper than its siblings (<c>cache.read</c>), and a cost that lives beside the counters
    /// rather than at the root (<c>part.cost</c>).
    /// </summary>
    [Fact]
    public async Task Dotted_paths_reach_the_container_the_nested_buckets_and_the_sibling_cost()
    {
        var task = TaskId.New();

        var result = await RunAsync(StepStartLine + "\n" + StepFinishLine, task, OpenCodeMapping());

        var usage = Assert.Single(Usage(result.Events));
        Assert.Equal(900, usage.InputTokens);
        Assert.Equal(200, usage.CacheReadTokens);
        Assert.Equal(34, usage.CacheWriteTokens);
        Assert.Equal(0.0123m, usage.CostUsd);
        // No per-model breakdown exists, so the report names no model rather than one docketd
        // invented — the same honesty the aggregate path keeps for every harness.
        Assert.Null(usage.Model);
    }

    /// <summary>
    /// The second semantic key, and the reason it is not cosmetic. OpenCode publishes
    /// <c>output = output - reasoning</c> (<c>session.ts:373</c>), while
    /// <see cref="UsageReportedEvent.ReasoningOutputTokens"/> is defined as a portion <em>of</em>
    /// output and is therefore excluded from the plane's <c>TotalTokens</c>. Left alone, the two
    /// conventions multiply out to tokens that simply vanish — 80 of them on this one line, and
    /// most of the spend on a thinking model. The fold restores the invariant the wire assumes.
    /// </summary>
    [Fact]
    public async Task A_disjoint_reasoning_count_is_folded_back_into_output()
    {
        var task = TaskId.New();

        var result = await RunAsync(StepStartLine + "\n" + StepFinishLine, task, OpenCodeMapping());

        var usage = Assert.Single(Usage(result.Events));
        // 120 reported + 80 reasoning: what the model actually produced.
        Assert.Equal(200, usage.OutputTokens);
        // Still reported as the portion it now genuinely is.
        Assert.Equal(80, usage.ReasoningOutputTokens);
    }

    /// <summary>
    /// The same line under the default (claude/Codex) semantics, so the fold is demonstrably the
    /// declaration's doing and not a new unconditional behaviour. This is the regression guard
    /// that matters most for the other two harnesses.
    /// </summary>
    [Fact]
    public async Task Reasoning_stays_inside_output_when_the_profile_does_not_say_otherwise()
    {
        var task = TaskId.New();
        var subset = OpenCodeMapping();
        subset.Remove("usage_reasoning_is_subset");

        var result = await RunAsync(StepStartLine + "\n" + StepFinishLine, task, subset);

        var usage = Assert.Single(Usage(result.Events));
        Assert.Equal(120, usage.OutputTokens);
        Assert.Equal(80, usage.ReasoningOutputTokens);
    }

    /// <summary>
    /// Per-step deltas accumulate, so what leaves docketd is cumulative-to-date and the plane's
    /// high-water upsert stays correct across a dropped event. The reasoning fold composes with
    /// the accumulation rather than fighting it: each step's output is folded first, then summed.
    /// </summary>
    [Fact]
    public async Task Per_step_deltas_accumulate_into_a_cumulative_total()
    {
        var task = TaskId.New();

        var result = await RunAsync(
            string.Join("\n", StepStartLine, StepFinishLine, SecondStepFinishLine),
            task,
            OpenCodeMapping());

        var reports = Usage(result.Events);
        Assert.Equal(2, reports.Count);
        Assert.Equal(900, reports[0].InputTokens);
        // 900 + 500, and (120+80) + (60+40) for output.
        Assert.Equal(1400, reports[1].InputTokens);
        Assert.Equal(300, reports[1].OutputTokens);
        Assert.Equal(300, reports[1].CacheReadTokens);
        Assert.Equal(120, reports[1].ReasoningOutputTokens);
        // Cost is NOT accumulated by docketd: each step's line states its own, and the plane
        // keeps the high-water mark. Asserted so a future accumulator change has to face it.
        Assert.Equal(0.0077m, reports[1].CostUsd);
    }

    /// <summary>
    /// The one-segment guarantee that made #144 cost no new key: a path with no dots behaves
    /// exactly like the bare name it used to be. Claude's own default shape, read through the
    /// path-based reader, still comes out whole.
    /// </summary>
    [Fact]
    public async Task A_one_segment_path_is_the_bare_name_it_always_was()
    {
        var task = TaskId.New();
        const string claudeResult = """
            {"type":"result","subtype":"success","session_id":"24d17fa6","total_cost_usd":0.011285,"usage":{"input_tokens":2,"output_tokens":4,"cache_read_input_tokens":18282,"cache_creation_input_tokens":17178}}
            """;

        var result = await RunAsync(claudeResult, task);

        var usage = Assert.Single(Usage(result.Events));
        Assert.Equal(2, usage.InputTokens);
        Assert.Equal(4, usage.OutputTokens);
        Assert.Equal(18282, usage.CacheReadTokens);
        Assert.Equal(17178, usage.CacheWriteTokens);
        Assert.Equal(0.011285m, usage.CostUsd);
    }

    /// <summary>
    /// The trap the OpenCode integration surfaced, characterized rather than merely refused: had
    /// the profile reached for <c>step_start</c> twice — a natural mistake on a stream with six
    /// types — the reader's early return on a session-init line would have swallowed the usage.
    /// <c>RunnerConfig</c> now fails that config at load; this pins what it would otherwise do.
    /// </summary>
    [Fact]
    public async Task A_usage_type_equal_to_system_type_would_be_swallowed_by_the_init_return()
    {
        var task = TaskId.New();
        var collided = OpenCodeMapping();
        collided["usage_type"] = "step_start";
        collided["system_type"] = "step_start";

        var result = await RunAsync(StepStartLine + "\n" + StepFinishLine, task, collided);

        Assert.Equal("ses_7f3ad2c1", result.SessionId);
        Assert.Empty(Usage(result.Events));
    }
}
