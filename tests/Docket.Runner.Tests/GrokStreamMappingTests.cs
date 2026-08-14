using Docket.Contracts;
using Docket.Core;
using Microsoft.Extensions.Time.Testing;

namespace Docket.Runner.Tests;

/// <summary>
/// §10 event relay against Grok Build's <c>grok -p --output-format streaming-messages-json</c>
/// stream. Unlike Codex/OpenCode, this harness emits the Anthropic Messages NDJSON shape the
/// built-in Claude defaults already describe — so the profile declares <c>events.source:
/// terminal</c> with <b>no mapping</b>.
///
/// <para>Fixtures are a captured run of <c>grok 1.0.3</c> (2026-08-14), shortened: thinking
/// bodies and signatures dropped, session id replaced. The shapes (init / assistant+tool_use /
/// result+usage) match <c>~/.grok/docs/user-guide/14-headless-mode.md</c>.</para>
/// </summary>
public sealed class GrokStreamMappingTests
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

    // Captured 2026-08-14 against grok 1.0.3, then shortened. session_id is a stand-in.
    private const string InitLine = """
        {"type":"system","subtype":"init","session_id":"ses-grok-fixture","apiKeySource":"user","model":"grok-4.6","cwd":"/tmp/fixture","permissionMode":"bypassPermissions","tools":["list_dir"],"slash_commands":[],"mcp_servers":[],"skills":[],"uuid":"u0"}
        """;

    private const string ToolUseLine = """
        {"type":"assistant","message":{"id":"msg_0","type":"message","role":"assistant","model":"grok-4.6","content":[{"type":"text","text":"ok"},{"type":"tool_use","id":"call_1","name":"list_dir","input":{"target_directory":"."}}],"stop_reason":"tool_use","stop_sequence":null},"parent_tool_use_id":null,"session_id":"ses-grok-fixture","uuid":"u1"}
        """;

    private const string ResultLine = """
        {"type":"result","subtype":"success","is_error":false,"num_turns":2,"result":"ok","stop_reason":"end_turn","total_cost_usd":0.005,"usage":{"input_tokens":11025,"output_tokens":176,"cache_read_input_tokens":17280,"cache_creation_input_tokens":0},"modelUsage":{"grok-4.6-build":{"inputTokens":11025,"outputTokens":176,"cacheReadInputTokens":17280,"cacheCreationInputTokens":0,"costUSD":0.005}},"session_id":"ses-grok-fixture","uuid":"u2"}
        """;

    private const string Stream = InitLine + "\n" + ToolUseLine + "\n" + ResultLine + "\n";

    /// <summary>
    /// The ACP <c>streaming-json</c> shape: <c>tool_call</c> / <c>end</c>, no
    /// <c>system</c>/<c>init</c>. Empty mapping must read nothing — the profile that
    /// picks this format by mistake loses the session ref and the progress clock.
    /// </summary>
    private const string AcpStream = """
        {"type":"thought","data":"thinking"}
        {"type":"tool_call","toolCallId":"c1","toolName":"list_dir","status":"in_progress"}
        {"type":"end","sessionId":"ses-wrong-format","usage":{"input_tokens":1}}
        """;

    [Fact]
    public async Task Empty_mapping_reads_session_ref_tool_call_and_usage_from_streaming_messages_json()
    {
        var task = TaskId.New();
        var result = await RunAsync(Stream, task);

        Assert.Equal("ses-grok-fixture", result.SessionId);
        var tool = Assert.Single(result.Events.OfType<ToolCallEvent>());
        Assert.Equal("list_dir", tool.Tool);
        var usage = Assert.Single(result.Events.OfType<UsageReportedEvent>());
        Assert.Equal(11025, usage.InputTokens);
        Assert.Equal(176, usage.OutputTokens);
        Assert.Equal(17280, usage.CacheReadTokens);
        Assert.Equal(0, usage.CacheWriteTokens);
        Assert.Equal(0.005m, usage.CostUsd);
        Assert.Equal("grok-4.6-build", usage.Model);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task The_acp_streaming_json_shape_is_invisible_to_the_claude_defaults()
    {
        var result = await RunAsync(AcpStream, TaskId.New());

        Assert.Null(result.SessionId);
        Assert.Empty(result.Events.OfType<ToolCallEvent>());
        Assert.Empty(result.Events.OfType<UsageReportedEvent>());
    }

    [Fact]
    public async Task Cache_buckets_are_already_disjoint_so_the_reader_does_not_subtract()
    {
        // Captured json usage: input_tokens is uncached; cache_read sits beside it.
        // usage_cached_is_subset defaults false (claude). Subtracting would under-count.
        var result = await RunAsync(Stream, TaskId.New());
        var usage = Assert.Single(result.Events.OfType<UsageReportedEvent>());
        Assert.Equal(11025, usage.InputTokens);
        Assert.Equal(17280, usage.CacheReadTokens);
    }
}
