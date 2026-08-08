using Docket.Core;
using Microsoft.Extensions.Time.Testing;

namespace Docket.Runner.Tests;

/// <summary>
/// §10 event relay against a <b>second real harness</b>: what
/// <see cref="EventsSource.Terminal"/> can and cannot read out of OpenAI Codex CLI's
/// <c>codex exec --json</c> stream. This is the half of the BYO-harness promise that can
/// be established without the <c>codex</c> binary — the fixtures are Codex's own
/// documented event shapes and the assertions are about <em>Docket's</em> parser, so the
/// findings hold whether or not a real Codex run is available.
///
/// <para><b>Provenance of the fixtures.</b> Every line below is the canonical sample
/// stream from OpenAI's non-interactive-mode documentation
/// (<c>https://developers.openai.com/codex/noninteractive.md</c>), which states: "When you
/// enable <c>--json</c>, <c>stdout</c> becomes a JSON Lines (JSONL) stream so you can
/// capture every event Codex emits while it's running. Event types include
/// <c>thread.started</c>, <c>turn.started</c>, <c>turn.completed</c>, <c>turn.failed</c>,
/// <c>item.*</c>, and <c>error</c>." They are doc-derived, not captured from a run on this
/// machine — the shapes are therefore as trustworthy as the vendor's docs and no more.
/// A real-run capture belongs to the opt-in <c>RealCodex</c> tier.</para>
///
/// <para><b>What these facts establish, in order.</b> (1) Docket's built-in claude
/// defaults read <em>nothing</em> from a Codex stream — so a Codex profile that omits
/// <c>events.mapping</c> silently loses its session ref. (2) The §11 resume ref
/// <em>is</em> reachable by config alone, via a mapping that points the init
/// discriminator at <c>thread.started</c> and the id key at <c>thread_id</c> — no code
/// change, which is §10's promise holding for a harness it was not designed against.
/// (3) <c>tool-call</c> events are <b>not</b> reachable by any mapping, because Codex
/// nests one tool call per event object where the reader requires an array of content
/// blocks — the seam overrides property <em>names</em>, never <em>shape</em>. That third
/// one is a real limit of the current seam and the gap worth filing.</para>
/// </summary>
public sealed class CodexStreamMappingTests
{
    /// <summary>Drives the reader over an NDJSON blob and reports everything a test
    /// asserts on: the drained events, how many lines counted as liveness, and the
    /// captured session id. Mirrors <see cref="TerminalEventReaderTests"/>' harness so
    /// the two harnesses are compared on identical machinery.</summary>
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

    // ── Codex's documented `codex exec --json` stream ───────────────────────────

    /// <summary>The only line carrying a resumable id, and the §11 seam's whole hope: the
    /// id is <c>thread_id</c>, top-level, and the line has <b>no</b> <c>subtype</c>
    /// property at all — which is exactly why the claude defaults miss it.</summary>
    private const string CodexThreadStarted =
        """{"type":"thread.started","thread_id":"0199a213-81c0-7800-8aa1-bbab2a035a53"}""";
    private const string CodexTurnStarted =
        """{"type":"turn.started"}""";
    /// <summary>A tool call in flight. Note the shape: <c>item</c> is a single object, not
    /// an array of content blocks — the structural mismatch fact 3 pins.</summary>
    private const string CodexItemStarted =
        """{"type":"item.started","item":{"id":"item_1","type":"command_execution","command":"bash -lc ls","status":"in_progress"}}""";
    private const string CodexItemCompleted =
        """{"type":"item.completed","item":{"id":"item_3","type":"agent_message","text":"Repo contains docs, sdk, and examples directories."}}""";
    private const string CodexTurnCompleted =
        """{"type":"turn.completed","usage":{"input_tokens":24763,"cached_input_tokens":24448,"output_tokens":122,"reasoning_output_tokens":0}}""";

    private static string CodexStream() => string.Join(
        '\n', CodexThreadStarted, CodexTurnStarted, CodexItemStarted, CodexItemCompleted, CodexTurnCompleted);

    /// <summary>
    /// The mapping that makes Codex's <c>thread.started</c> line satisfy the reader's
    /// claude-shaped <c>system</c> + <c>subtype: init</c> gate. The trick, and it is a
    /// legitimate use of the seam rather than a hack: Codex has no sub-discriminator, so
    /// <c>subtype_key</c> is pointed back at <c>type</c> and <c>init_subtype</c> at the
    /// same <c>thread.started</c> value the outer check already matched. Both checks then
    /// read the one property Codex does emit, and the id key does the real work.
    /// </summary>
    private static Dictionary<string, string> CodexMapping() => new()
    {
        ["system_type"] = "thread.started",
        ["subtype_key"] = "type",
        ["init_subtype"] = "thread.started",
        ["session_id_key"] = "thread_id",
    };

    // ── The three facts ────────────────────────────────────────────────────────

    /// <summary>
    /// A Codex profile that declares <c>events.source: terminal</c> and no <c>mapping</c>
    /// reads NOTHING: no session ref (so §11 resume degrades to a documented cold start,
    /// silently) and no tool calls. The claude defaults are defaults, not universals —
    /// this is the cost of omitting the mapping, stated as an assertion so it cannot be
    /// rediscovered the hard way.
    /// </summary>
    [Fact]
    public async Task Codex_stream_under_the_built_in_claude_defaults_yields_nothing()
    {
        var task = TaskId.New();

        var r = await RunAsync(CodexStream(), task);

        Assert.Null(r.SessionId);   // no `system`/`init` line exists in a Codex stream
        Assert.Empty(r.Events);     // and no content-block array to find a tool_use in
    }

    /// <summary>
    /// §11's resume ref survives the harness swap <b>by configuration alone</b>: with the
    /// Codex mapping, the reader captures <c>thread_id</c> off <c>thread.started</c> — the
    /// id <c>codex exec resume &lt;SESSION_ID&gt;</c> takes. Without a session ref there is
    /// nothing to resume and every redispatch is a cold start, so this single mapping is
    /// what keeps park/resume and <c>preserve</c> meaningful for a Codex worker.
    /// </summary>
    [Fact]
    public async Task Codex_thread_id_is_captured_as_the_session_ref_through_the_mapping_seam()
    {
        var task = TaskId.New();

        var r = await RunAsync(CodexStream(), task, CodexMapping());

        Assert.Equal("0199a213-81c0-7800-8aa1-bbab2a035a53", r.SessionId);
    }

    /// <summary>
    /// The seam's real limit, and the gap worth filing: <c>tool-call</c> events are
    /// unreachable for Codex under <em>any</em> mapping. The reader requires
    /// <c>message</c> → <c>content</c> to be an <b>array</b> of blocks and reads the tool
    /// name off a block; Codex puts exactly one tool call in <c>item</c>, an object. The
    /// mapping seam renames properties — it cannot change nesting or arity, so no
    /// combination of the eleven keys bridges object-vs-array.
    ///
    /// <para>This test tries the most favourable mapping available (treat
    /// <c>item.started</c> as the assistant turn, <c>item</c> as the message wrapper) and
    /// asserts it still produces nothing, so the claim is "no mapping works", not "the
    /// obvious mapping does not".</para>
    ///
    /// <para><b>Why this is a degradation and not a blocker.</b> Per-task liveness on the
    /// plane is refreshed by the periodic <c>alive</c> the daemon emits for every live
    /// task regardless of events source (<c>RunnerDaemon.EmitAliveEvents</c>), so a Codex
    /// task is not requeued merely for being quiet. What is lost is the progress clock:
    /// the §10 no-progress ceiling (30 minutes) becomes the only thing governing a Codex
    /// worker, and a wedged one cannot be told from a busy one before it fires.</para>
    /// </summary>
    [Fact]
    public async Task Codex_tool_calls_are_unreachable_because_the_seam_renames_but_cannot_reshape()
    {
        var task = TaskId.New();
        var bestEffort = CodexMapping();
        // The most generous reading available: Codex's per-tool-call event as the
        // "assistant turn", and its `item` object as the message wrapper.
        bestEffort["assistant_type"] = "item.started";
        bestEffort["message_key"] = "item";
        bestEffort["content_key"] = "item";           // there is no array-valued property
        bestEffort["block_type_key"] = "type";
        bestEffort["tool_use_block_type"] = "command_execution";
        bestEffort["tool_name_key"] = "command";

        var r = await RunAsync(CodexStream(), task, bestEffort);

        Assert.Equal("0199a213-81c0-7800-8aa1-bbab2a035a53", r.SessionId); // the ref still lands
        Assert.DoesNotContain(r.Events, e => e is ToolCallEvent);          // but no progress signal
    }

    /// <summary>
    /// The short aliveness clock is safe: every well-formed Codex line bumps per-task
    /// activity, exactly as a claude line does, because the reader records liveness
    /// before it dispatches on any harness-specific discriminator. All five documented
    /// event lines count.
    /// </summary>
    [Fact]
    public async Task Every_codex_event_line_records_activity_even_with_no_mapping()
    {
        var task = TaskId.New();

        var r = await RunAsync(CodexStream(), task);

        Assert.Equal(5, r.RecordCount);
    }
}
