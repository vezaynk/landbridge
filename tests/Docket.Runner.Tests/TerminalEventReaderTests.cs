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
        var recordCount = 0;
        string? capturedSession = null;

        var reader = new TerminalEventReader(
            task,
            ring,
            recordActivity: _ => Interlocked.Increment(ref recordCount),
            mapping ?? new Dictionary<string, string>(),
            new FakeTimeProvider(),
            onSessionId: id => capturedSession = id);

        using (var sr = new StringReader(ndjson))
            await reader.ReadToEndAsync(sr, CancellationToken.None);

        ring.Complete();
        var events = new List<RunnerEvent>();
        await foreach (var item in ring.ReadAllAsync(CancellationToken.None))
            events.Add(item.Event);

        return new Result(events, recordCount, capturedSession);
    }

    private sealed record Result(List<RunnerEvent> Events, int RecordCount, string? SessionId);

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
        // Only tool-calls reach the ring; liveness is the RecordActivity callback.
        Assert.All(r.Events, e => Assert.IsType<ToolCallEvent>(e));
    }

    [Fact]
    public async Task Every_well_formed_object_line_records_activity_stray_lines_do_not()
    {
        var task = TaskId.New();
        var ndjson = string.Join('\n',
            RealClaudeInit,            // 1 well-formed
            "",                        // blank — not activity
            "   ",                     // whitespace — not activity
            "plain banner text",       // not JSON — not activity
            "[1,2,3]",                 // JSON but not an object — not activity
            RealClaudeThinking,        // 2 well-formed
            RealClaudeToolUse,         // 3 well-formed
            RealClaudeResult);         // 4 well-formed

        var r = await RunAsync(ndjson, task);

        Assert.Equal(4, r.RecordCount);
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

    // Plain concatenation, not raw-string interpolation: JSON's trailing "}}"
    // would collide with a "$$" raw literal's interpolation delimiters.
    private static string ToolUseLine(string tool) =>
        "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":"
        + "[{\"type\":\"tool_use\",\"id\":\"t\",\"name\":\"" + tool + "\",\"input\":{}}]}}";
}
