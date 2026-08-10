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
/// <c>events.mapping</c> loses its session ref, though no longer <em>silently</em>: the
/// drain now reports the one-line degradation notice issue #111 asked for. (2) The §11
/// resume ref <em>is</em> reachable by config alone, via a mapping that points the init
/// discriminator at <c>thread.started</c> and the id key at <c>thread_id</c> — no code
/// change, which is §10's promise holding for a harness it was not designed against.
/// (3) <c>tool-call</c> events are reachable too, through the flat mapping mode
/// (<c>tool_event_type</c> + <c>tool_name_path</c>) that issue #111 added — <b>this fact was
/// the opposite before that change</b>, and its old text is preserved on the test itself,
/// because the limit it described (renaming cannot reshape) is still true of the eleven
/// rename keys. What changed is that the seam now describes the flat shape directly instead
/// of trying to bridge it.</para>
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
        var warnings = new List<string>();

        var reader = new TerminalEventReader(
            task,
            ring,
            recordActivity: _ => Interlocked.Increment(ref recordCount),
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

        return new Result(events, recordCount, capturedSession, warnings);
    }

    private sealed record Result(
        List<RunnerEvent> Events, int RecordCount, string? SessionId, List<string> Warnings);

    private static List<string> ToolNames(IEnumerable<RunnerEvent> events) =>
        events.OfType<ToolCallEvent>().Select(e => e.Tool).ToList();

    // ── Codex's documented `codex exec --json` stream ───────────────────────────

    /// <summary>The only line carrying a resumable id, and the §11 seam's whole hope: the
    /// id is <c>thread_id</c>, top-level, and the line has <b>no</b> <c>subtype</c>
    /// property at all — which is exactly why the claude defaults miss it.</summary>
    private const string CodexThreadStarted =
        """{"type":"thread.started","thread_id":"0199a213-81c0-7800-8aa1-bbab2a035a53"}""";
    private const string CodexTurnStarted =
        """{"type":"turn.started"}""";
    /// <summary>A shell tool call in flight. Note the shape: <c>item</c> is a single object, not
    /// an array of content blocks — the structural mismatch fact 3 pins, and what the flat
    /// mapping mode reads directly.</summary>
    private const string CodexItemStarted =
        """{"type":"item.started","item":{"id":"item_1","type":"command_execution","command":"bash -lc ls","status":"in_progress"}}""";
    /// <summary>An MCP tool call in flight — the second tool-ish item shape, and the reason
    /// <c>tool_name_path</c> takes alternatives: Codex names a shell call in <c>command</c> and
    /// an MCP call in <c>tool</c>, both under the same <c>item.started</c> type. Fields per
    /// <c>McpToolCallItem { server, tool, arguments, result, error, status }</c>
    /// (<c>codex-rs/exec/src/exec_events.rs:286</c> at <c>rust-v0.147.0</c>).</summary>
    private const string CodexMcpItemStarted =
        """{"type":"item.started","item":{"id":"item_2","type":"mcp_tool_call","server":"docket","tool":"get_task","status":"in_progress"}}""";
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

    /// <summary>
    /// The full Codex mapping: the session-ref keys above plus the flat tool-call mode
    /// (issue #111). <c>item.started</c> rather than <c>item.completed</c> is deliberate — the
    /// point of a <c>tool-call</c> is the progress clock, and a call is announced when it
    /// starts, so a 25-minute build reports progress at minute zero instead of only on
    /// completion, which is exactly the window the no-progress ceiling would otherwise close.
    /// Both name paths are declared because Codex spells the name per item kind.
    /// </summary>
    private static Dictionary<string, string> CodexFlatMapping()
    {
        var mapping = CodexMapping();
        mapping["tool_event_type"] = "item.started";
        mapping["tool_name_path"] = "item.command, item.tool";
        return mapping;
    }

    // ── The three facts ────────────────────────────────────────────────────────

    /// <summary>
    /// A Codex profile that declares <c>events.source: terminal</c> and no <c>mapping</c>
    /// reads NOTHING: no session ref (so §11 resume degrades to a cold start) and no tool
    /// calls. The claude defaults are defaults, not universals — this is the cost of omitting
    /// the mapping, stated as an assertion so it cannot be rediscovered the hard way.
    ///
    /// <para>What issue #111 changed here is the last word of that sentence: the cost is no
    /// longer paid <em>silently</em>. Five well-formed event lines went by and the mapping
    /// read none of them, and the drain says so exactly once, naming the task — which is the
    /// whole of G4. The message must also say what it cost, since "no events" alone does not
    /// tell an operator that resume just became a cold start.</para>
    /// </summary>
    [Fact]
    public async Task Codex_stream_under_the_built_in_claude_defaults_yields_nothing_but_says_so()
    {
        var task = TaskId.New();

        var r = await RunAsync(CodexStream(), task);

        Assert.Null(r.SessionId);   // no `system`/`init` line exists in a Codex stream
        Assert.Empty(r.Events);     // and no content-block array to find a tool_use in

        var warning = Assert.Single(r.Warnings);
        Assert.Contains(task.Value.ToString(), warning, StringComparison.Ordinal);
        Assert.Contains("5 JSON event line(s)", warning, StringComparison.Ordinal);
        Assert.Contains("declares no events.mapping", warning, StringComparison.Ordinal);
        Assert.Contains("cold start", warning, StringComparison.Ordinal);
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
        // A ref is extraction, so this profile is productive and gets no degradation notice
        // even though it maps no tool calls — the warning marks streams read to no effect,
        // not streams read incompletely.
        Assert.Empty(r.Warnings);
    }

    /// <summary>
    /// The other half of G4: a mapping that <em>is</em> declared but matches nothing — the
    /// realistic failure once mapping keys exist, since a typo or a key copied from another
    /// harness looks configured. The notice distinguishes the two causes, because "you have no
    /// mapping" and "your mapping is wrong" send an operator to different places.
    /// </summary>
    [Fact]
    public async Task A_declared_codex_mapping_that_matches_nothing_is_reported_as_such()
    {
        var task = TaskId.New();
        var typo = CodexFlatMapping();
        typo["session_id_key"] = "threadId";        // Codex emits thread_id
        typo["tool_name_path"] = "item.commandline"; // Codex emits item.command

        var r = await RunAsync(CodexStream(), task, typo);

        Assert.Null(r.SessionId);
        Assert.Empty(r.Events);
        var warning = Assert.Single(r.Warnings);
        Assert.Contains("events.mapping matched none of them", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Flipped by issue #111 — this fact used to assert the opposite.</b> Codex's
    /// <c>tool-call</c> IS reachable by configuration alone, through the flat mapping mode.
    ///
    /// <para><b>The limit this fact used to pin, kept for the record.</b> Before #111 the
    /// eleven mapping keys only renamed <em>within</em> claude's nesting: the reader required
    /// <c>message</c> → <c>content</c> to be an <b>array</b> of blocks and read the tool name
    /// off a block, while Codex puts exactly one tool call in <c>item</c>, an object. The old
    /// version of this test tried the most favourable rename available — <c>assistant_type:
    /// item.started</c>, <c>message_key: item</c>, <c>content_key: item</c> (there is no
    /// array-valued property to point at), <c>tool_use_block_type: command_execution</c>,
    /// <c>tool_name_key: command</c> — and asserted it still produced nothing, so the claim
    /// was "no mapping works", not "the obvious mapping does not". That claim was true of the
    /// rename-only seam and is still true of it: what changed is that the seam now also
    /// carries a shape, not that renaming learned to reshape.</para>
    ///
    /// <para><b>What makes it work now.</b> <c>tool_event_type</c> declares which
    /// <c>type</c> value <em>is</em> a tool call and <c>tool_name_path</c> a dotted path to
    /// the name, so the object-per-event shape is described rather than bridged. Still no
    /// scripting and no Codex knowledge in <c>docketd</c>: two more declarative keys in the
    /// same mapping bag, and the event emitted is the same frozen <c>tool-call</c> member.
    /// The progress clock a Codex worker was missing — the §10 no-progress ceiling being its
    /// only governor — is restored by config.</para>
    /// </summary>
    [Fact]
    public async Task Codex_tool_calls_are_recoverable_through_the_flat_mapping_mode()
    {
        var task = TaskId.New();

        var r = await RunAsync(CodexStream(), task, CodexFlatMapping());

        Assert.Equal("0199a213-81c0-7800-8aa1-bbab2a035a53", r.SessionId); // the ref still lands
        Assert.Equal(["bash -lc ls"], ToolNames(r.Events));                // and now so does progress
        Assert.Empty(r.Warnings);                                          // nothing silent to report
    }

    /// <summary>
    /// Both of Codex's tool-ish item shapes land, from one profile: a shell call named in
    /// <c>item.command</c> and an MCP call named in <c>item.tool</c>. Alternatives are why —
    /// a single path would silently drop whichever half it did not name, and for a Docket
    /// worker the dropped half would be the MCP calls into the plane itself.
    /// </summary>
    [Fact]
    public async Task Both_codex_tool_name_paths_are_tried_so_shell_and_mcp_calls_both_report()
    {
        var task = TaskId.New();
        var ndjson = string.Join('\n', CodexThreadStarted, CodexItemStarted, CodexMcpItemStarted);

        var r = await RunAsync(ndjson, task, CodexFlatMapping());

        Assert.Equal(["bash -lc ls", "get_task"], ToolNames(r.Events));
    }

    /// <summary>
    /// The absence of the named property is the item-kind discriminator: Codex's
    /// <c>agent_message</c> and <c>reasoning</c> items ride the same <c>item.started</c> type
    /// as a tool call and carry neither <c>command</c> nor <c>tool</c>, so they emit nothing.
    /// This is what lets flat mode work without a second "which item kind is a tool" key —
    /// and it is worth pinning, because the alternative failure is a progress event per
    /// assistant sentence, which would make the progress clock meaningless.
    /// </summary>
    [Fact]
    public async Task Non_tool_codex_items_on_the_tool_event_type_emit_nothing()
    {
        var task = TaskId.New();
        var ndjson = string.Join('\n',
            CodexThreadStarted,
            """{"type":"item.started","item":{"id":"item_9","type":"agent_message","text":"Let me look."}}""",
            """{"type":"item.started","item":{"id":"item_10","type":"reasoning","text":"Thinking."}}""");

        var r = await RunAsync(ndjson, task, CodexFlatMapping());

        Assert.Empty(ToolNames(r.Events));
        Assert.Equal("0199a213-81c0-7800-8aa1-bbab2a035a53", r.SessionId);
        // The session ref counts as extraction, so a stream that only lacks tool calls is
        // not reported as silent — the warning is about a mapping that reads NOTHING.
        Assert.Empty(r.Warnings);
    }

    /// <summary>
    /// One tool call, one event: <c>item.completed</c> repeats the same
    /// <c>command_execution</c> item that <c>item.started</c> announced (plus its exit code),
    /// and mapping <c>item.started</c> means the pair counts once. Choosing
    /// <c>item.completed</c> instead would report the same call one build later; choosing both
    /// is not expressible, which is the intended shape of "flat".
    /// </summary>
    [Fact]
    public async Task The_completed_twin_of_a_started_tool_call_does_not_double_count()
    {
        var task = TaskId.New();
        var ndjson = string.Join('\n',
            CodexItemStarted,
            """{"type":"item.completed","item":{"id":"item_1","type":"command_execution","command":"bash -lc ls","exit_code":0,"status":"completed"}}""");

        var r = await RunAsync(ndjson, task, CodexFlatMapping());

        Assert.Equal(["bash -lc ls"], ToolNames(r.Events));
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
