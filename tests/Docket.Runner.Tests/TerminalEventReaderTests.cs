using Docket.Core;
using Microsoft.Extensions.Time.Testing;

namespace Docket.Runner.Tests;

/// <summary>
/// §10 event relay, <see cref="EventsSource.Terminal"/>: the
/// <see cref="TerminalEventReader"/> maps a harness's stdout NDJSON to the frozen
/// event vocabulary. Fixtures are the claude <c>--output-format stream-json</c>
/// shapes captured from a real run, plus hand-crafted edge cases (a malformed
/// line, a multi-tool_use turn, a remapped harness shape).
/// </summary>
public sealed class TerminalEventReaderTests
{
    /// <summary>Drives the reader over an NDJSON blob and reports everything a test
    /// asserts on: the drained events, how many lines counted as liveness, and the
    /// captured session id.</summary>
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

    private sealed record Result(
        List<RunnerEvent> Events, string? SessionId, List<string> Warnings);

    private static List<string> ToolNames(IEnumerable<RunnerEvent> events, TaskId task) =>
        events.OfType<ToolCallEvent>().Where(e => e.Task == task).Select(e => e.Tool).ToList();

    // A trimmed-but-faithful capture of the real `claude -p ... --output-format
    // stream-json --verbose` shapes: the system/init carrying the session id, a
    // thinking-only assistant turn, then an assistant turn whose content array
    // holds a tool_use block naming the tool (the real block also carries id/input/
    // caller, kept here to prove the reader ignores the extra fields).
    private const string RealClaudeInit =
        """{"type":"system","subtype":"init","cwd":"/tmp","session_id":"a4bbb0fd-fc3d-4825-9635-0b478017d4f5","tools":["Bash"],"model":"claude-haiku-4-5-20251001","permissionMode":"default"}""";
    private const string RealClaudeThinking =
        """{"type":"assistant","message":{"model":"claude-haiku-4-5-20251001","role":"assistant","content":[{"type":"thinking","thinking":"I'll list the files.","signature":"abc"}]},"session_id":"a4bbb0fd-fc3d-4825-9635-0b478017d4f5"}""";
    private const string RealClaudeToolUse =
        """{"type":"assistant","message":{"model":"claude-haiku-4-5-20251001","role":"assistant","content":[{"type":"tool_use","id":"toolu_01TroG4oaokz7tCVhVP1F5Wp","name":"Bash","input":{"command":"ls -la"},"caller":{"type":"direct"}}]},"session_id":"a4bbb0fd-fc3d-4825-9635-0b478017d4f5"}""";
    private const string RealClaudeResult =
        """{"is_error":false,"num_turns":2,"session_id":"a4bbb0fd-fc3d-4825-9635-0b478017d4f5","type":"result","subtype":"success"}""";

    [Fact]
    public async Task Maps_real_claude_stream_to_tool_calls_and_captures_session_id()
    {
        var task = TaskId.New();
        var ndjson = string.Join('\n', RealClaudeInit, RealClaudeThinking, RealClaudeToolUse, RealClaudeResult);

        var r = await RunAsync(ndjson, task);

        Assert.Equal("a4bbb0fd-fc3d-4825-9635-0b478017d4f5", r.SessionId);
        Assert.Equal(["Bash"], ToolNames(r.Events, task));
        // Only tool-calls reach the ring; the session ref rides its own callback.
        Assert.All(r.Events, e => Assert.IsType<ToolCallEvent>(e));
    }

    /// <summary>
    /// Which line shapes count as a well-formed event line — the denominator of the
    /// silent-stream warning, and the reader's only notion of "this stream is producing
    /// something". Blank, whitespace-only, non-JSON, and JSON-but-not-an-object lines are all
    /// excluded; a real harness emits every one of them (banners, progress spinners, blank
    /// separators) and none is evidence about the mapping.
    ///
    /// <para>Asserted through the warning text because that is where the count is
    /// <em>production-visible</em>: an operator reads "parsed as N JSON event line(s)" and
    /// decides whether their mapping is wrong. The mapping here deliberately describes
    /// nothing, which is what makes the warning fire and the count observable — the four
    /// claude lines parse as objects and yield neither a session ref nor a tool-call.</para>
    ///
    /// <para>This previously asserted a <c>RecordActivity</c> callback count. That callback fed
    /// a runner-local activity clock no production code read, so the number it counted had no
    /// consequence anywhere; the same classification is now pinned on the surface that does.</para>
    /// </summary>
    [Fact]
    public async Task Only_well_formed_object_lines_count_as_event_lines_stray_output_does_not()
    {
        var task = TaskId.New();
        var ndjson = string.Join('\n',
            RealClaudeInit,            // 1 well-formed object
            "",                        // blank — not an event line
            "   ",                     // whitespace — not an event line
            "plain banner text",       // not JSON — not an event line
            "[1,2,3]",                 // JSON but not an object — not an event line
            RealClaudeThinking,        // 2 well-formed
            RealClaudeToolUse,         // 3 well-formed
            RealClaudeResult);         // 4 well-formed

        // A mapping that matches none of the four, so nothing is extracted and the
        // silent-stream warning reports the count.
        var r = await RunAsync(ndjson, task, new Dictionary<string, string>
        {
            ["system_type"] = "no-such-type",
            ["assistant_type"] = "no-such-type-either",
        });

        Assert.Contains("4 JSON event line(s)", Assert.Single(r.Warnings), StringComparison.Ordinal);
        Assert.Empty(r.Events);
        Assert.Null(r.SessionId);
    }

    [Fact]
    public async Task Skips_a_malformed_line_and_keeps_draining()
    {
        var task = TaskId.New();
        var ndjson = string.Join('\n',
            RealClaudeInit,
            ToolUseLine("Grep"),
            "{ this is not valid json",     // malformed — must be skipped, not fatal
            "}{ also broken",               // malformed
            ToolUseLine("Edit"));

        var r = await RunAsync(ndjson, task);

        // Both tool calls on either side of the garbage survived.
        Assert.Equal(["Grep", "Edit"], ToolNames(r.Events, task));
    }

    [Fact]
    public async Task Multi_tool_use_turn_emits_one_event_per_block()
    {
        var task = TaskId.New();
        // One assistant message whose content array holds two tool_use blocks
        // interleaved with a text block — each tool_use is its own progress signal.
        var line =
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Read","input":{}},{"type":"text","text":"and now"},{"type":"tool_use","id":"t2","name":"Write","input":{}}]}}""";

        var r = await RunAsync(line, task);

        Assert.Equal(["Read", "Write"], ToolNames(r.Events, task));
    }

    [Fact]
    public async Task Session_id_is_captured_only_from_system_init_not_from_other_lines()
    {
        var task = TaskId.New();
        // A result line carrying a session_id arrives with NO preceding init; the
        // reader must not treat a non-init line as the session-id source.
        var r1 = await RunAsync(RealClaudeResult, task);
        Assert.Null(r1.SessionId);

        // With the init present, it is captured.
        var r2 = await RunAsync(string.Join('\n', RealClaudeInit, RealClaudeResult), task);
        Assert.Equal("a4bbb0fd-fc3d-4825-9635-0b478017d4f5", r2.SessionId);
    }

    [Fact]
    public async Task Subagent_lineage_is_not_fabricated_from_a_task_tool_use()
    {
        var task = TaskId.New();
        // A `Task` tool_use is claude's only subagent signal, and it carries no clean
        // (AgentId, ParentAgentId) pair — so it maps to a plain ToolCallEvent, never
        // a SubagentSpawnedEvent (documented dashboard empty-state, §10).
        var r = await RunAsync(ToolUseLine("Task"), task);

        Assert.Equal(["Task"], ToolNames(r.Events, task));
        Assert.DoesNotContain(r.Events, e => e is SubagentSpawnedEvent);
    }

    [Fact]
    public async Task Mapping_generalizes_a_different_harness_stream_shape()
    {
        var task = TaskId.New();
        // A hypothetical harness that names everything differently. With the mapping
        // it parses; the SAME bytes without the mapping produce nothing — proving
        // the shape is config, not code (§10).
        var mapping = new Dictionary<string, string>
        {
            ["type_key"] = "kind",
            ["system_type"] = "meta",
            ["assistant_type"] = "turn",
            ["subtype_key"] = "event",
            ["init_subtype"] = "start",
            ["session_id_key"] = "sid",
            ["message_key"] = "payload",
            ["content_key"] = "blocks",
            ["block_type_key"] = "kind",
            ["tool_use_block_type"] = "call",
            ["tool_name_key"] = "tool",
        };
        var ndjson = string.Join('\n',
            """{"kind":"meta","event":"start","sid":"other-harness-42"}""",
            """{"kind":"turn","payload":{"blocks":[{"kind":"call","tool":"HttpGet"}]}}""");

        var mapped = await RunAsync(ndjson, task, mapping);
        Assert.Equal("other-harness-42", mapped.SessionId);
        Assert.Equal(["HttpGet"], ToolNames(mapped.Events, task));

        var unmapped = await RunAsync(ndjson, task);
        Assert.Null(unmapped.SessionId);
        Assert.Empty(unmapped.Events);
    }

    // ── Flat tool-call mode: the dotted-path resolver (§10, issue #111) ────────

    /// <summary>The two keys that switch flat mode on, with a single one-level path.</summary>
    private static Dictionary<string, string> FlatMapping(string path) => new()
    {
        ["tool_event_type"] = "tool",
        ["tool_name_path"] = path,
    };

    [Fact]
    public async Task Flat_mode_reads_a_tool_name_through_a_dotted_path()
    {
        var task = TaskId.New();
        var line = """{"type":"tool","item":{"kind":"exec","tool":"Bash"}}""";

        var r = await RunAsync(line, task, FlatMapping("item.tool"));

        Assert.Equal(["Bash"], ToolNames(r.Events, task));
    }

    [Fact]
    public async Task Flat_mode_walks_a_path_deeper_than_one_level()
    {
        var task = TaskId.New();
        // Nothing about the resolver is limited to one hop; a harness that nests further
        // is describable too. Three segments, and the sibling branch is ignored.
        var line = """{"type":"tool","payload":{"call":{"name":"Grep"},"other":{"name":"NotThis"}}}""";

        var r = await RunAsync(line, task, FlatMapping("payload.call.name"));

        Assert.Equal(["Grep"], ToolNames(r.Events, task));
    }

    /// <summary>
    /// Every way a path can fail to describe the line resolves to "not a tool call" and emits
    /// nothing — never a partial or fabricated event. Absence is load-bearing: it is how flat
    /// mode filters the non-tool events that share the tool <c>type</c> value, so each of these
    /// misses has to be silent rather than an error.
    /// </summary>
    [Theory]
    // A missing segment at the end, and one in the middle.
    [InlineData("""{"type":"tool","item":{"kind":"exec"}}""", "item.tool")]
    [InlineData("""{"type":"tool","item":{"kind":"exec","tool":"Bash"}}""", "item.nested.tool")]
    // The path lands on a non-string: number, bool, null, object, array. A tool name is a
    // string, and coercing any of these would invent one.
    [InlineData("""{"type":"tool","item":{"tool":7}}""", "item.tool")]
    [InlineData("""{"type":"tool","item":{"tool":true}}""", "item.tool")]
    [InlineData("""{"type":"tool","item":{"tool":null}}""", "item.tool")]
    [InlineData("""{"type":"tool","item":{"tool":{"name":"Bash"}}}""", "item.tool")]
    [InlineData("""{"type":"tool","item":{"tool":["Bash"]}}""", "item.tool")]
    // The path lands on an empty string — present but naming nothing.
    [InlineData("""{"type":"tool","item":{"tool":""}}""", "item.tool")]
    // An intermediate segment is not an object, so there is nothing to walk into.
    [InlineData("""{"type":"tool","item":"Bash"}""", "item.tool")]
    [InlineData("""{"type":"tool","item":[{"tool":"Bash"}]}""", "item.tool")]
    // No array indexing: "0" is read as a property name, which an array does not have.
    [InlineData("""{"type":"tool","items":[{"tool":"Bash"}]}""", "items.0.tool")]
    // No wildcards either: "*" is just a property name that no harness emits.
    [InlineData("""{"type":"tool","item":{"tool":"Bash"}}""", "item.*")]
    // Property names are exact — no case-insensitive fallback.
    [InlineData("""{"type":"tool","item":{"tool":"Bash"}}""", "item.Tool")]
    public async Task Flat_mode_emits_nothing_when_the_path_does_not_describe_the_line(
        string line, string path)
    {
        var task = TaskId.New();

        var r = await RunAsync(line, task, FlatMapping(path));

        Assert.Empty(r.Events);
    }

    [Fact]
    public async Task Flat_mode_only_fires_on_its_own_type_value()
    {
        var task = TaskId.New();
        // The same tool-shaped object under a different `type` is not a tool call: the
        // discriminator is the whole point, or every line carrying the path would count.
        var line = """{"type":"item.updated","item":{"tool":"Bash"}}""";

        var r = await RunAsync(line, task, FlatMapping("item.tool"));

        Assert.Empty(r.Events);
    }

    [Fact]
    public async Task Flat_mode_emits_at_most_one_event_per_line_from_the_first_matching_path()
    {
        var task = TaskId.New();
        // Both alternatives resolve on this line. Flat means one tool call per line, so the
        // first declared path wins and the second is not also emitted.
        var line = """{"type":"tool","item":{"tool":"Preferred","command":"fallback"}}""";

        var r = await RunAsync(line, task, FlatMapping("item.tool,item.command"));

        Assert.Equal(["Preferred"], ToolNames(r.Events, task));
    }

    [Fact]
    public async Task Flat_mode_coexists_with_the_block_array_mode_on_one_profile()
    {
        var task = TaskId.New();
        // A harness need not be all one shape: claude's nested assistant turn and a flat
        // per-call event can both be declared, because they key off different `type` values.
        var mapping = FlatMapping("item.tool");
        var ndjson = string.Join('\n', ToolUseLine("Read"), """{"type":"tool","item":{"tool":"Exec"}}""");

        var r = await RunAsync(ndjson, task, mapping);

        Assert.Equal(["Read", "Exec"], ToolNames(r.Events, task));
    }

    [Fact]
    public async Task Flat_mode_stays_off_unless_both_keys_are_declared()
    {
        var task = TaskId.New();
        var line = """{"type":"tool","item":{"tool":"Bash"}}""";

        // Either key alone is inert (and rejected at config load, so this is belt-and-braces
        // for a mapping constructed in code).
        var typeOnly = await RunAsync(line, task, new Dictionary<string, string> { ["tool_event_type"] = "tool" });
        var pathOnly = await RunAsync(line, task, new Dictionary<string, string> { ["tool_name_path"] = "item.tool" });

        Assert.Empty(typeOnly.Events);
        Assert.Empty(pathOnly.Events);
    }

    [Fact]
    public async Task A_malformed_tool_name_path_never_matches_rather_than_matching_loosely()
    {
        var task = TaskId.New();
        var line = """{"type":"tool","item":{"tool":"Bash"}}""";

        // Empty and padded segments are config-load errors; if one reaches the reader anyway
        // it reads nothing, rather than being trimmed into a path that happens to work.
        foreach (var path in new[] { "item..tool", "item . tool", ".item.tool", "item.tool." })
        {
            var r = await RunAsync(line, task, FlatMapping(path));
            Assert.Empty(r.Events);
        }

        // Whitespace AROUND an alternative is tolerated, so a comma-separated list can be
        // written with spaces after the commas.
        var spaced = await RunAsync(line, task, FlatMapping("  item.missing ,  item.tool  "));
        Assert.Equal(["Bash"], ToolNames(spaced.Events, task));
    }

    // ── The silent-stream warning (§11, issue #111) ────────────────────────────

    [Fact]
    public async Task A_productive_terminal_stream_is_never_warned_about()
    {
        var task = TaskId.New();
        var ndjson = string.Join('\n', RealClaudeInit, RealClaudeToolUse, RealClaudeResult);

        var r = await RunAsync(ndjson, task);

        Assert.Empty(r.Warnings);
    }

    [Fact]
    public async Task One_extracted_signal_is_enough_to_stay_quiet()
    {
        var task = TaskId.New();

        // Session ref only, no tool call.
        var refOnly = await RunAsync(string.Join('\n', RealClaudeInit, RealClaudeResult), task);
        Assert.Empty(refOnly.Warnings);

        // Tool call only, no session ref — a resumed worker's stream has no init line, and
        // warning about it would fire on every §11 resume.
        var toolOnly = await RunAsync(ToolUseLine("Bash"), task);
        Assert.Empty(toolOnly.Warnings);
    }

    [Fact]
    public async Task A_silent_terminal_stream_is_warned_about_exactly_once_per_task()
    {
        var task = TaskId.New();
        // Forty well-formed lines of a shape the mapping does not describe: one notice for the
        // task, not one per line — an operator's log must not be the thing that breaks.
        var ndjson = string.Join('\n',
            Enumerable.Repeat("""{"type":"progress","step":"still working"}""", 40));

        var r = await RunAsync(ndjson, task);

        var warning = Assert.Single(r.Warnings);
        Assert.Contains("40 JSON event line(s)", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stream_with_no_parseable_event_line_is_not_reported_as_a_mapping_fault()
    {
        var task = TaskId.New();
        // Nothing parsed, so there is no evidence about the mapping either way: a harness that
        // is not streaming JSON, or a worker that died before its first line, is a different
        // diagnosis that its exit code and transcript already carry. Warning here would fire
        // on every fast failure and train operators to ignore the line.
        var r = await RunAsync("not json at all\n\n[1,2,3]\n\"bare string\"", task);

        Assert.Empty(r.Warnings);
        Assert.Empty(r.Events);
    }

    [Fact]
    public async Task An_empty_stream_is_not_reported_either()
    {
        var task = TaskId.New();

        var r = await RunAsync("", task);

        Assert.Empty(r.Warnings);
    }

    // Plain concatenation, not raw-string interpolation: JSON's trailing "}}"
    // would collide with a "$$" raw literal's interpolation delimiters.
    private static string ToolUseLine(string tool) =>
        "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":"
        + "[{\"type\":\"tool_use\",\"id\":\"t\",\"name\":\"" + tool + "\",\"input\":{}}]}}";
}
