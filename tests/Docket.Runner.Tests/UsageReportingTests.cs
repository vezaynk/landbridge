using Docket.Contracts;
using Docket.Core;
using Docket.Runner;
using Microsoft.Extensions.Time.Testing;

namespace Docket.Runner.Tests;

/// <summary>
/// §10 telemetry ingest: <see cref="TerminalEventReader"/> mapping a harness's own usage report
/// onto <see cref="UsageReportedEvent"/>.
///
/// <para><b>The fixtures are real, and that matters here more than anywhere else in this suite.</b>
/// Claude Code's documentation states that <c>--output-format json</c> "includes
/// <c>total_cost_usd</c> and a per-model cost breakdown" but never enumerates the sub-field
/// spellings, so the shapes below were captured from an actual <c>claude -p</c> run (2.1.226)
/// rather than transcribed from prose. Two casings in one payload — snake_case in the aggregate
/// <c>usage</c> object, camelCase inside <c>modelUsage</c> — is exactly the kind of thing a
/// doc-derived parser gets wrong and a captured one cannot.</para>
///
/// <para>The Codex shapes come from its source at <c>rust-v0.147.0</c>
/// (<c>exec/src/exec_events.rs</c>, <c>protocol/src/protocol.rs</c>), which is where the
/// cache-subset semantics the normalization exists for are actually defined.</para>
/// </summary>
public sealed class UsageReportingTests
{
    private static async Task<List<UsageReportedEvent>> RunAsync(
        string ndjson,
        TaskId task,
        IReadOnlyDictionary<string, string>? mapping = null)
    {
        var ring = new OutboundEventRing(capacity: 256);
        var reader = new TerminalEventReader(
            task,
            ring,
            mapping ?? new Dictionary<string, string>(),
            new FakeTimeProvider());

        using (var sr = new StringReader(ndjson))
            await reader.ReadToEndAsync(sr, CancellationToken.None);

        ring.Complete();
        var events = new List<UsageReportedEvent>();
        await foreach (var item in ring.ReadAllAsync(CancellationToken.None))
            if (item.Event is UsageReportedEvent u && u.Task == task)
                events.Add(u);
        return events;
    }

    /// <summary>
    /// The real <c>result</c> line, trimmed to the fields this reads but with every name and
    /// nesting verbatim from the captured run. Two models on one dispatch is not a contrived
    /// case: it is what a single "reply with ok" prompt actually produced, because the main loop
    /// and an auxiliary call ran on different models.
    /// </summary>
    private const string ClaudeResultLine = """
        {"type":"result","subtype":"success","is_error":false,"num_turns":1,"session_id":"24d17fa6","total_cost_usd":0.011285,"usage":{"input_tokens":2,"output_tokens":4,"cache_read_input_tokens":18282,"cache_creation_input_tokens":17178,"cache_creation":{"ephemeral_1h_input_tokens":17178,"ephemeral_5m_input_tokens":0},"service_tier":"standard"},"modelUsage":{"claude-sonnet-5[1m]":{"inputTokens":2,"outputTokens":4,"cacheReadInputTokens":18282,"cacheCreationInputTokens":17178,"costUSD":0.1086186,"contextWindow":1000000},"claude-haiku-4-5-20251001":{"inputTokens":521,"outputTokens":13,"cacheReadInputTokens":0,"cacheCreationInputTokens":0,"costUSD":0.000586,"contextWindow":200000}}}
        """;

    // ── Claude: per-model reporting, cost included ──────────────────────────────

    [Fact]
    public async Task Claude_reports_one_event_per_model_with_its_own_tokens_and_cost()
    {
        var task = TaskId.New();

        var events = await RunAsync(ClaudeResultLine, task);

        // Per model, NOT one collapsed total: a dispatch whose auxiliary work ran on a cheaper
        // model has two real figures, and the whole point of the §12 view is showing both.
        Assert.Equal(2, events.Count);
        var sonnet = events.Single(e => e.Model == "claude-sonnet-5[1m]");
        var haiku = events.Single(e => e.Model == "claude-haiku-4-5-20251001");

        Assert.Equal(2, sonnet.InputTokens);
        Assert.Equal(4, sonnet.OutputTokens);
        Assert.Equal(18282, sonnet.CacheReadTokens);
        Assert.Equal(17178, sonnet.CacheWriteTokens);
        Assert.Equal(0.1086186m, sonnet.CostUsd);

        Assert.Equal(521, haiku.InputTokens);
        Assert.Equal(13, haiku.OutputTokens);
        Assert.Equal(0.000586m, haiku.CostUsd);

        // Claude's stream exposes no reasoning breakdown, and an absent one stays null rather
        // than becoming a zero that reads as "measured, none".
        Assert.All(events, e => Assert.Null(e.ReasoningOutputTokens));
    }

    [Fact]
    public async Task Claude_input_is_not_reduced_because_its_buckets_are_already_disjoint()
    {
        // The measured proof that claude's `input_tokens` EXCLUDES cache: 2 input tokens
        // alongside 35k of cache for the prompt "Reply with exactly: ok". So the subset
        // normalization must NOT apply here — subtracting would clamp a real count to zero.
        var task = TaskId.New();

        var events = await RunAsync(ClaudeResultLine, task);

        var sonnet = events.Single(e => e.Model == "claude-sonnet-5[1m]");
        Assert.Equal(2, sonnet.InputTokens);
        Assert.Equal(18282, sonnet.CacheReadTokens);
    }

    [Fact]
    public async Task A_result_line_reporting_nothing_emits_nothing()
    {
        // A report of all-zeros is not a measurement, and writing one would put a row of zeros
        // on the dashboard that reads exactly like a measured "this was free".
        var task = TaskId.New();

        var events = await RunAsync(
            """{"type":"result","usage":{"input_tokens":0,"output_tokens":0},"modelUsage":{}}""",
            task);

        Assert.Empty(events);
    }

    [Fact]
    public async Task Unknown_line_types_are_skipped_rather_than_treated_as_malformed()
    {
        // `rate_limit_event` is a real line type the published stream-json docs do not list —
        // it appeared first in the captured run. A parser that treated an unlisted type as an
        // error would fail on a stream that is working perfectly.
        var task = TaskId.New();

        var events = await RunAsync(
            """{"type":"rate_limit_event","rate_limit":{"status":"allowed"}}""" + "\n" + ClaudeResultLine,
            task);

        Assert.Equal(2, events.Count);
    }

    // ── Codex: tokens only, and the cache-subset normalization ──────────────────

    /// <summary>
    /// The mapping a Codex profile declares. Every value here is a fact about Codex's stream,
    /// declared as data — docketd holds no harness knowledge in code (§10).
    /// </summary>
    private static Dictionary<string, string> CodexMapping() => new(StringComparer.Ordinal)
    {
        ["usage_type"] = "turn.completed",
        ["usage_key"] = "usage",
        ["usage_input_key"] = "input_tokens",
        ["usage_output_key"] = "output_tokens",
        ["usage_cache_read_key"] = "cached_input_tokens",
        ["usage_cache_write_key"] = "cache_write_input_tokens",
        ["usage_reasoning_key"] = "reasoning_output_tokens",
        // Codex reports no cost anywhere, so there is no key to read one from.
        ["usage_cost_key"] = "",
        // Codex names no model on any stream event, so there is no per-model object either.
        ["usage_models_key"] = "",
        // THE semantic key: Codex's cached_input_tokens is a SUBSET of its input_tokens.
        ["usage_cached_is_subset"] = "true",
        // Codex reports per-turn, not cumulative.
        ["usage_is_cumulative"] = "false",
    };

    [Fact]
    public async Task Codex_cached_tokens_are_subtracted_from_input_so_the_buckets_stay_disjoint()
    {
        // The finding this whole normalization exists for. Codex counts the WHOLE prompt in
        // input_tokens with cached_input_tokens as a subset of it — its own non_cached_input()
        // subtracts one from the other (protocol/src/protocol.rs). Reporting them as-is would
        // double-count every cached token the moment the four buckets are summed, which on a
        // cache-heavy worker is most of the number.
        var task = TaskId.New();

        var events = await RunAsync(
            """
            {"type":"turn.completed","usage":{"input_tokens":1000,"cached_input_tokens":900,"cache_write_input_tokens":50,"output_tokens":30,"reasoning_output_tokens":12}}
            """,
            task,
            CodexMapping());

        var e = Assert.Single(events);
        Assert.Equal(100, e.InputTokens);      // 1000 reported - 900 cached
        Assert.Equal(900, e.CacheReadTokens);
        Assert.Equal(50, e.CacheWriteTokens);
        Assert.Equal(30, e.OutputTokens);
        // Summing the buckets now counts the prompt once, which is the property that makes the
        // §12 total comparable across harnesses.
        Assert.Equal(1080, e.InputTokens + e.OutputTokens + e.CacheReadTokens + e.CacheWriteTokens);
    }

    [Fact]
    public async Task Codex_reasoning_tokens_ride_along_and_never_inflate_output()
    {
        // reasoning_output_tokens is a portion OF output_tokens, so it is carried for visibility
        // and excluded from output — anyone adding it in would count those tokens twice.
        var task = TaskId.New();

        var events = await RunAsync(
            """
            {"type":"turn.completed","usage":{"input_tokens":10,"cached_input_tokens":0,"output_tokens":30,"reasoning_output_tokens":12}}
            """,
            task,
            CodexMapping());

        var e = Assert.Single(events);
        Assert.Equal(30, e.OutputTokens);
        Assert.Equal(12, e.ReasoningOutputTokens);
    }

    [Fact]
    public async Task Codex_reports_no_cost_and_none_is_invented_for_it()
    {
        // Codex has no cost figure on its stream or in its metrics. The event carries null, and
        // the §12 view renders "not reported" — never a derived number, and never $0.00.
        var task = TaskId.New();

        var events = await RunAsync(
            """{"type":"turn.completed","usage":{"input_tokens":10,"cached_input_tokens":0,"output_tokens":5}}""",
            task,
            CodexMapping());

        Assert.Null(Assert.Single(events).CostUsd);
    }

    [Fact]
    public async Task Per_turn_reports_are_accumulated_so_the_wire_carries_cumulative_totals()
    {
        // Codex reports each turn's own usage. The plane keeps a high-water mark per (task,
        // model), which is only drop-tolerant if what arrives is cumulative — so the reader
        // accumulates rather than making the plane sum deltas it might have missed.
        var task = TaskId.New();

        var events = await RunAsync(
            """
            {"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":0,"output_tokens":10}}
            {"type":"turn.completed","usage":{"input_tokens":200,"cached_input_tokens":0,"output_tokens":20}}
            """,
            task,
            CodexMapping());

        Assert.Equal(2, events.Count);
        Assert.Equal(100, events[0].InputTokens);
        Assert.Equal(300, events[1].InputTokens);   // cumulative, not the second turn's 200
        Assert.Equal(30, events[1].OutputTokens);
    }

    // ── Model attribution: harness-sourced, or honestly absent ──────────────────

    [Fact]
    public async Task A_harness_that_names_no_model_reports_real_tokens_unattributed()
    {
        // The scoping consequence, stated as a fact rather than worked around: Codex names no
        // model on any exec event, so its tokens arrive attributed to nothing. §12 renders "not
        // reported" against real counts. Nothing substitutes a model here — not a profile
        // declaration and not the spawn argv — because a model the PLANE asserted, shown in a
        // section labelled "reported by the harness", is the mislabelling §2 principle 2 forbids.
        var task = TaskId.New();

        var events = await RunAsync(
            """{"type":"turn.completed","usage":{"input_tokens":10,"cached_input_tokens":0,"output_tokens":5}}""",
            task,
            CodexMapping());

        var e = Assert.Single(events);
        Assert.Null(e.Model);
        Assert.Equal(10, e.InputTokens);
        Assert.Equal(5, e.OutputTokens);
    }

    [Fact]
    public async Task Claude_models_come_from_the_harness_and_nothing_else_can_supply_them()
    {
        // The other half of the same rule: where the harness DOES name models, those names are
        // what is carried — per model, several per dispatch, exactly as reported.
        var task = TaskId.New();

        var events = await RunAsync(ClaudeResultLine, task);

        Assert.Equal(
            new[] { "claude-haiku-4-5-20251001", "claude-sonnet-5[1m]" },
            events.Select(e => e.Model).OrderBy(m => m, StringComparer.Ordinal).ToArray());
    }
}
