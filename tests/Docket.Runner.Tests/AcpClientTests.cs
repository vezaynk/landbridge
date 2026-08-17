using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Docket.Core;
using Microsoft.Extensions.Time.Testing;

namespace Docket.Runner.Tests;

/// <summary>
/// §10: what docketd's Agent Client Protocol client says, and what it makes of what it
/// hears back.
///
/// <para><b>Why these are conversation tests, not parser tests.</b> The event-relay suites
/// this replaced fed a fixed NDJSON blob to a reader and asserted on what came out, because
/// in stream mode docketd only ever listened. ACP inverts that: an agent that is never
/// spoken to produces nothing at all, so the thing under test is a two-way exchange and the
/// fixture has to be an agent, not a transcript. Hence <see cref="FakeAgent"/> — a scripted
/// peer that answers requests the way the spec says an agent must, and can be told to answer
/// them wrongly.</para>
///
/// <para><b>Provenance of the shapes.</b> Message framing (newline-delimited JSON-RPC 2.0,
/// no embedded newlines), the <c>initialize</c>/<c>session/new</c>/<c>session/prompt</c>
/// sequence, the <c>session/update</c> discriminators, and the
/// <c>session/request_permission</c> option/outcome shapes are all from the published ACP
/// specification, not from a captured run — no ACP agent binary was available here. That
/// makes these facts exactly as trustworthy as the spec, and a real-binary tier
/// (<c>RealOpenCode</c>, whose <c>opencode acp</c> is a native ACP agent) is where they get
/// confirmed against an implementation.</para>
/// </summary>
public sealed class AcpClientTests
{
    private static readonly TaskId Task1 = TaskId.New();

    // ── the conversation ────────────────────────────────────────────────────────

    /// <summary>
    /// The whole point of the mode: docketd speaks first, and in the order the spec
    /// requires. An agent that is merely spawned does nothing, so this sequence — and the
    /// fact that the prompt travels in <c>session/prompt</c> rather than the argv — IS the
    /// protocol migration.
    /// </summary>
    [Fact]
    public async Task Drives_initialize_then_session_new_then_prompt()
    {
        var agent = new FakeAgent();
        var run = await RunAsync(agent, Request("Do the task."));

        Assert.Equal(
            ["initialize", "session/new", "session/prompt"],
            agent.MethodsReceived);

        var init = agent.Received("initialize");
        Assert.Equal(AcpClient.LatestProtocolVersion, init.GetProperty("params").GetProperty("protocolVersion").GetInt32());
        Assert.Equal("docketd", init.GetProperty("params").GetProperty("clientInfo").GetProperty("name").GetString());

        // Declared UNSUPPORTED on purpose: a Docket worker has its own shell and its own
        // work dir, so it never needs its supervisor to read or write files for it.
        var fs = init.GetProperty("params").GetProperty("clientCapabilities").GetProperty("fs");
        Assert.False(fs.GetProperty("readTextFile").GetBoolean());
        Assert.False(init.GetProperty("params").GetProperty("clientCapabilities").GetProperty("terminal").GetBoolean());

        var prompt = agent.Received("session/prompt").GetProperty("params");
        Assert.Equal("sess_1", prompt.GetProperty("sessionId").GetString());
        var block = prompt.GetProperty("prompt").EnumerateArray().Single();
        Assert.Equal("text", block.GetProperty("type").GetString());
        Assert.Equal("Do the task.", block.GetProperty("text").GetString());

        Assert.Equal("end_turn", run.Client.StopReason);
    }

    /// <summary>
    /// §13, and the seam that repays the migration on its own: the plane's MCP server
    /// crosses on <c>session/new</c> as a session parameter, bearer header and all. In
    /// stream mode this is a file whose path substitutes into the argv — which claude can
    /// take and Codex and OpenCode cannot, forcing an operator-written static file plus an
    /// environment variable to smuggle the per-dispatch token in. Here there is nothing to
    /// write and nothing to smuggle.
    /// </summary>
    [Fact]
    public async Task Pins_advertised_config_options_after_session_new()
    {
        // OpenCode ACP defaults to opencode/big-pickle. The pin is a
        // session/set_config_option after the agent has advertised the value.
        var agent = new FakeAgent
        {
            AdvertiseOptions = new Dictionary<string, string>
            {
                ["model"] = "anthropic/claude-haiku-4-5-20251001",
            },
        };
        var request = Request("go") with
        {
            ConfigOptions = new Dictionary<string, string>
            {
                ["model"] = "anthropic/claude-haiku-4-5-20251001",
            },
        };
        await RunAsync(agent, request);

        Assert.Equal("anthropic/claude-haiku-4-5-20251001", agent.PinnedOptions["model"]);
        Assert.Equal(
            ["initialize", "session/new", "session/set_config_option", "session/prompt"],
            agent.MethodsReceived);
    }

    [Fact]
    public async Task Does_not_pin_a_config_option_the_agent_did_not_advertise()
    {
        var agent = new FakeAgent();
        await RunAsync(agent, Request("go") with
        {
            ConfigOptions = new Dictionary<string, string>
            {
                ["model"] = "anthropic/claude-haiku-4-5-20251001",
            },
        });

        Assert.Empty(agent.PinnedOptions);
        Assert.DoesNotContain("session/set_config_option", agent.MethodsReceived);
    }

    [Fact]
    public async Task Pins_each_advertised_config_option_and_skips_the_rest()
    {
        var agent = new FakeAgent
        {
            AdvertiseOptions = new Dictionary<string, string>
            {
                ["model"] = "anthropic/claude-haiku-4-5-20251001",
                ["mode"] = "code",
            },
        };
        await RunAsync(agent, Request("go") with
        {
            ConfigOptions = new Dictionary<string, string>
            {
                ["model"] = "anthropic/claude-haiku-4-5-20251001",
                ["mode"] = "code",
                ["thought_level"] = "high",
            },
        });

        Assert.Equal("anthropic/claude-haiku-4-5-20251001", agent.PinnedOptions["model"]);
        Assert.Equal("code", agent.PinnedOptions["mode"]);
        Assert.False(agent.PinnedOptions.ContainsKey("thought_level"));
        Assert.Equal(2, agent.MethodsReceived.Count(m => m == "session/set_config_option"));
    }

    [Fact]
    public async Task Hands_the_planes_mcp_server_over_on_session_new()
    {
        var agent = new FakeAgent();
        await RunAsync(agent, Request("go"));

        var p = agent.Received("session/new").GetProperty("params");
        Assert.Equal("/work/task-1", p.GetProperty("cwd").GetString());

        var server = p.GetProperty("mcpServers").EnumerateArray().Single();
        Assert.Equal("http", server.GetProperty("type").GetString());
        Assert.Equal("docket", server.GetProperty("name").GetString());
        Assert.Equal("https://plane.example/mcp", server.GetProperty("url").GetString());

        var header = server.GetProperty("headers").EnumerateArray().Single();
        Assert.Equal("Authorization", header.GetProperty("name").GetString());
        Assert.Equal("Bearer dkt_w_token", header.GetProperty("value").GetString());
    }

    /// <summary>
    /// §11 resume: the ref arrives as a JSON-RPC result rather than being fished out of a
    /// log line, and is reported to the plane exactly once so a later park can carry it.
    /// </summary>
    [Fact]
    public async Task Reports_the_session_id_from_session_new()
    {
        var agent = new FakeAgent();
        var run = await RunAsync(agent, Request("go"));

        Assert.Equal("sess_1", run.SessionId);
    }

    // ── tool calls and the progress clock ───────────────────────────────────────

    /// <summary>
    /// One call, one event — the §10 progress signal, with the de-duplication that keeps it
    /// honest. ACP announces a call with <c>tool_call</c> and then reports it repeatedly
    /// through <c>tool_call_update</c> as it moves pending → in_progress → completed, so
    /// without the <c>toolCallId</c> guard a single call would move the plane's clock three
    /// times. This is the same mistake in a new dialect: a Codex profile that maps
    /// <c>item.updated</c> alongside <c>item.started</c> triple-counts identically.
    /// </summary>
    [Fact]
    public async Task Reports_each_tool_call_once_across_its_updates()
    {
        var agent = new FakeAgent
        {
            DuringPrompt =
            [
                ToolCall("call_1", "tool_call", "Reading configuration file", "read", "pending"),
                ToolCall("call_1", "tool_call_update", "Reading configuration file", "read", "in_progress"),
                ToolCall("call_1", "tool_call_update", "Reading configuration file", "read", "completed"),
                ToolCall("call_2", "tool_call", "Running tests", "execute", "pending"),
            ],
        };

        var run = await RunAsync(agent, Request("go"));

        Assert.Equal(["Reading configuration file", "Running tests"], run.ToolNames);
    }

    /// <summary>
    /// ACP names a call for a human and categorises it separately. The title is what the
    /// §12 event log wants to show; the kind is the honest fallback for an agent that sends
    /// no title, and it beats inventing a name or dropping the progress signal entirely.
    /// </summary>
    [Fact]
    public async Task Falls_back_to_the_tool_kind_when_an_agent_sends_no_title()
    {
        var agent = new FakeAgent
        {
            DuringPrompt = [ToolCall("call_1", "tool_call", title: null, kind: "execute", status: "pending")],
        };

        var run = await RunAsync(agent, Request("go"));

        Assert.Equal(["execute"], run.ToolNames);
    }

    /// <summary>
    /// Conversation content — message chunks, thoughts, plans — has no member in the frozen
    /// runner vocabulary and deliberately gets none. §12 capture keeps the text; the event
    /// ring carries signals the plane acts on, and an agent thinking out loud is not one.
    /// </summary>
    [Fact]
    public async Task Ignores_session_updates_that_are_not_tool_calls()
    {
        var agent = new FakeAgent
        {
            DuringPrompt =
            [
                """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"thinking"}}}}""",
                """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_1","update":{"sessionUpdate":"plan","entries":[]}}}""",
            ],
        };

        var run = await RunAsync(agent, Request("go"));

        Assert.Empty(run.ToolNames);
    }

    // ── permissions ─────────────────────────────────────────────────────────────

    /// <summary>
    /// §11 permission bridge: a plane allow maps onto the agent's <c>allow_once</c>, never
    /// <c>allow_always</c>. A standing bypass is not a Lead decision.
    /// </summary>
    [Fact]
    public async Task Answers_a_permission_request_with_the_agents_allow_once_option()
    {
        var agent = new FakeAgent { AskPermissionDuringPrompt = true };
        await RunAsync(agent, Request("go"), (_, _) => Task.FromResult(new AcpPermissionDecision(true, null)));

        var outcome = agent.PermissionResponse!.Value.GetProperty("result").GetProperty("outcome");
        Assert.Equal("selected", outcome.GetProperty("outcome").GetString());
        Assert.Equal("allow-once", outcome.GetProperty("optionId").GetString());
    }

    [Fact]
    public async Task Answers_a_denied_permission_request_with_the_agents_reject_option()
    {
        var agent = new FakeAgent
        {
            AskPermissionDuringPrompt = true,
            PermissionOptions =
                """[{"optionId":"allow-once","name":"Allow once","kind":"allow_once"},{"optionId":"reject-once","name":"Reject","kind":"reject_once"}]""",
        };
        await RunAsync(agent, Request("go"), (_, _) => Task.FromResult(new AcpPermissionDecision(false, "no")));

        var outcome = agent.PermissionResponse!.Value.GetProperty("result").GetProperty("outcome");
        Assert.Equal("selected", outcome.GetProperty("outcome").GetString());
        Assert.Equal("reject-once", outcome.GetProperty("optionId").GetString());
    }

    [Fact]
    public async Task Cancels_an_allow_when_the_agent_offered_only_allow_always()
    {
        var agent = new FakeAgent
        {
            AskPermissionDuringPrompt = true,
            PermissionOptions =
                """[{"optionId":"allow-always","name":"Always allow","kind":"allow_always"}]""",
        };
        await RunAsync(agent, Request("go"), (_, _) => Task.FromResult(new AcpPermissionDecision(true, null)));

        var outcome = agent.PermissionResponse!.Value.GetProperty("result").GetProperty("outcome");
        Assert.Equal("cancelled", outcome.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Cancels_a_permission_request_when_the_plane_is_not_wired()
    {
        var agent = new FakeAgent { AskPermissionDuringPrompt = true };
        await RunAsync(agent, Request("go"));

        var outcome = agent.PermissionResponse!.Value.GetProperty("result").GetProperty("outcome");
        Assert.Equal("cancelled", outcome.GetProperty("outcome").GetString());
    }

    /// <summary>
    /// An agent offering no options at all still has to be answered, or it blocks inside its
    /// tool call forever. <c>cancelled</c> is the spec's own word for "the client is not
    /// deciding this", which lets the agent move on.
    /// </summary>
    [Fact]
    public async Task Answers_an_optionless_permission_request_with_cancelled()
    {
        var agent = new FakeAgent { AskPermissionDuringPrompt = true, PermissionOptions = "[]" };
        await RunAsync(agent, Request("go"), (_, _) => Task.FromResult(new AcpPermissionDecision(true, null)));

        var outcome = agent.PermissionResponse!.Value.GetProperty("result").GetProperty("outcome");
        Assert.Equal("cancelled", outcome.GetProperty("outcome").GetString());
    }

    /// <summary>
    /// A request for a capability this client declared UNSUPPORTED is refused, not ignored.
    /// Silence would leave the agent waiting on an answer that is never coming — the worst
    /// outcome available, because it looks like a hang rather than a refusal.
    /// </summary>
    [Fact]
    public async Task Refuses_an_agent_request_for_an_unsupported_capability()
    {
        var agent = new FakeAgent { RequestDuringPrompt = "fs/read_text_file" };
        await RunAsync(agent, Request("go"));

        var error = agent.UnsupportedResponse!.Value.GetProperty("error");
        Assert.Equal(-32601, error.GetProperty("code").GetInt32());
        Assert.Contains("fs/read_text_file", error.GetProperty("message").GetString());
    }

    /// <summary>
    /// And the refusal is reported, not just sent. This is the quietest severe failure the
    /// mode has: an agent that delegates its shell or file access to the client gets -32601
    /// for everything, does no work, and produces a task that looks like a lazy model rather
    /// than a wiring fault. The three agents measured for the migration all carry their own
    /// tools, so this should never fire — which is exactly why it must be loud when it does.
    /// </summary>
    [Fact]
    public async Task Reports_that_it_declined_a_capability_the_agent_wanted()
    {
        var agent = new FakeAgent { RequestDuringPrompt = "terminal/create" };
        var run = await RunAsync(agent, Request("go"));

        Assert.Contains(
            run.Warnings,
            w => w.Contains("terminal/create") && w.Contains("client-side terminal"));
    }

    // ── the session outlives the turn (ideas/sessions.md stage 1) ───────────────

    /// <summary>
    /// The shape change, stated as a fact: the turn ending is not the conversation ending.
    /// Under the task model these were one observation — a worker that finished a turn had
    /// also exited — which is exactly why a question had to become a park and a redispatch.
    /// </summary>
    [Fact]
    public async Task A_finished_turn_does_not_end_the_session()
    {
        var agent = new FakeAgent { HoldOpenAfterTurn = true };
        var (client, drain) = Start(agent, Request("go"));

        await agent.WaitForTurnsAsync(1);

        // The turn is over and reported, and the conversation is still there to talk to.
        Assert.Equal(1, client.Turns);
        Assert.Equal("end_turn", client.StopReason);
        Assert.True(client.TryQueueFollowUp());

        await agent.WaitForTurnsAsync(2);
        agent.EndSession();
        await drain;
    }

    /// <summary>
    /// A follow-up is a real second turn on the same session — same sessionId, no respawn,
    /// no <c>session/load</c>, and the agent keeps whatever context it had. This is what
    /// "conversations that don't suspend on every message" buys.
    /// </summary>
    [Fact]
    public async Task A_follow_up_becomes_another_turn_on_the_same_session()
    {
        var agent = new FakeAgent { HoldOpenAfterTurn = true };
        var (client, drain) = Start(agent, Request("first"));

        await agent.WaitForTurnsAsync(1);
        Assert.True(client.TryQueueFollowUp());
        await agent.WaitForTurnsAsync(2);
        agent.EndSession();
        var run = await drain;

        // The follow-up is the profile's wake-up text, NOT a payload: the answer itself is
        // pulled by the worker over MCP, and that pull is the read receipt (§11).
        Assert.Equal(["first", "Read your assignment again."], agent.PromptTexts);

        // One session throughout: no second session/new, and the ref never changed.
        Assert.Single(agent.MethodsReceived, m => m == "session/new");
        Assert.DoesNotContain("session/load", agent.MethodsReceived);
        Assert.Equal("sess_1", run.SessionId);
    }

    /// <summary>
    /// §10 vocabulary: every turn reports why it ended. The task model could not carry this
    /// — a token ceiling, a refusal and a clean finish all arrived as "exited 0 without
    /// reporting" — and it matters more since ACP took away <c>--max-turns</c>, because
    /// <c>max_tokens</c> is now one of the few bounds that announces itself.
    /// </summary>
    [Fact]
    public async Task Each_turn_reports_the_agents_own_reason_for_ending()
    {
        var agent = new FakeAgent { HoldOpenAfterTurn = true, StopReasons = ["end_turn", "max_tokens"] };
        var (client, drain) = Start(agent, Request("first"));

        await agent.WaitForTurnsAsync(1);
        client.TryQueueFollowUp();
        await agent.WaitForTurnsAsync(2);
        agent.EndSession();
        var run = await drain;

        Assert.Equal(
            ["end_turn", "max_tokens"],
            run.Events.OfType<TurnEndedEvent>().Select(e => e.StopReason).ToList());
    }

    // ── ACP authenticate ────────────────────────────────────────────────────────
    //
    // Measured 2026-08-16: codex-acp 1.3.0 declares authMethods [api-key, chat-gpt] and
    // answers session/new with -32000 "Authentication required" until one is chosen. Before
    // this the whole codex tier produced two transcript lines — initialize, then the refusal
    // — and every codex cell failed identically. claude-agent-acp declares no methods and
    // needs none, which is why the step has to be conditional rather than part of every
    // handshake.

    /// <summary>
    /// The refusal is the trigger, not the capability list. An agent that asks for
    /// authentication gets it and the session opens on the retry.
    /// </summary>
    [Fact]
    public async Task An_agent_that_demands_authentication_gets_it_and_the_session_opens()
    {
        var agent = new FakeAgent { AuthMethods = ["api-key", "chat-gpt"], RequireAuthentication = true };
        var run = await RunAsync(agent, Request("go") with { AuthMethod = "api-key" });

        Assert.Equal(
            ["initialize", "session/new", "authenticate", "session/new", "session/prompt"],
            agent.MethodsReceived);
        Assert.Equal("api-key", agent.AuthenticatedWith);
        Assert.Equal("sess_1", run.SessionId);
    }

    /// <summary>
    /// The method is the profile's to name. Guessing the first declared one is how a
    /// headless worker ends up in a browser login.
    /// </summary>
    [Fact]
    public async Task Auth_required_without_a_profile_auth_method_fails()
    {
        var agent = new FakeAgent { AuthMethods = ["api-key", "chat-gpt"], RequireAuthentication = true };
        var (_, drain) = Start(agent, Request("go"));
        await agent.WaitForAsync("session/new");
        // The -32000 is queued in the same write that unblocked WaitForAsync; give
        // the client a turn to refuse the missing auth_method before we EOF.
        await Task.Delay(50);
        agent.EndSession();
        var run = await drain.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain("authenticate", agent.MethodsReceived);
        Assert.DoesNotContain("session/prompt", agent.MethodsReceived);

        var failure = Assert.Single(run.Events.OfType<AuthFailedEvent>());
        Assert.Equal("authenticate", failure.Operation);
        Assert.Equal("acp", failure.Target);
        Assert.Contains("auth_method", Assert.Single(run.Warnings, w => w.Contains("auth_method")));
    }

    /// <summary>
    /// A profile that names <c>auth_method</c> overrides the guess — the escape hatch for an
    /// agent whose first-listed method wants a browser.
    /// </summary>
    [Fact]
    public async Task A_profile_can_name_which_auth_method_to_use()
    {
        var agent = new FakeAgent { AuthMethods = ["chat-gpt", "api-key"], RequireAuthentication = true };
        await RunAsync(agent, Request("go") with { AuthMethod = "api-key" });

        Assert.Equal("api-key", agent.AuthenticatedWith);
    }

    /// <summary>
    /// An agent that never asks is never authenticated. This is the claude case, and the
    /// reason the step is lazy: a declared method means authentication is <em>available</em>,
    /// not required, so authenticating eagerly would be a round trip that can only fail
    /// against an agent already holding a credential.
    /// </summary>
    [Fact]
    public async Task An_agent_that_does_not_ask_is_never_authenticated()
    {
        var agent = new FakeAgent { AuthMethods = ["api-key"] };
        await RunAsync(agent, Request("go"));

        Assert.DoesNotContain("authenticate", agent.MethodsReceived);
        Assert.Null(agent.AuthenticatedWith);
    }

    /// <summary>
    /// A rejected credential fails the dispatch with the method named, and files the §11
    /// auth-failed event — it does not retry. A second refusal is the credential being wrong
    /// rather than missing, and re-authenticating on that would turn a bad key into a spin.
    /// </summary>
    [Fact]
    public async Task A_rejected_credential_names_the_method_and_does_not_spin()
    {
        var agent = new FakeAgent
        {
            AuthMethods = ["api-key", "chat-gpt"],
            RequireAuthentication = true,
            AuthenticateFails = true,
        };
        var run = await RunAsync(agent, Request("go") with { AuthMethod = "api-key" });

        Assert.Single(agent.MethodsReceived, m => m == "authenticate");
        Assert.DoesNotContain("session/prompt", agent.MethodsReceived);

        var failure = Assert.Single(run.Events.OfType<AuthFailedEvent>());
        Assert.Equal("authenticate", failure.Operation);
        Assert.Equal("api-key", failure.Target);

        // The operator-facing line has to carry both what was tried and what else was on
        // offer, or the fix is a guess.
        var warning = Assert.Single(run.Warnings, w => w.Contains("authenticate"));
        Assert.Contains("api-key", warning);
        Assert.Contains("chat-gpt", warning);
        Assert.Contains("auth_method", warning);
    }

    // ── §10 telemetry ingest / §12 accounting ───────────────────────────────────
    //
    // The migration was accepted on the understanding that ACP traded the four disjoint
    // token buckets for a context-window gauge. A real turn says otherwise: the buckets are
    // on PromptResponse.usage, and they reconcile against totalTokens exactly. The fixtures
    // below are verbatim from claude-agent-acp 0.68.0 and opencode 1.18.18 on 2026-08-16 —
    // copied rather than invented, because the point of these tests is that docketd reads
    // what real agents actually send.

    /// <summary>
    /// A turn's usage lands as one report with the buckets it named and the cost the session
    /// priced. Both come from the same real turn: the four buckets off the prompt response,
    /// the dollar figure off the <c>usage_update</c> that preceded it.
    /// </summary>
    [Fact]
    public async Task A_turns_token_buckets_and_the_sessions_cost_reach_the_plane()
    {
        var agent = new FakeAgent
        {
            DuringPrompt =
            [
                """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_1","update":{"sessionUpdate":"usage_update","used":23569,"size":1000000,"cost":{"amount":0.09490875,"currency":"USD"}}}}""",
            ],
            TurnUsage =
            [
                """{"inputTokens":6,"outputTokens":866,"cachedReadTokens":61019,"cachedWriteTokens":6701,"totalTokens":68592}""",
            ],
        };
        var (_, drain) = Start(agent, Request("go"));
        var run = await drain;

        var usage = Assert.Single(run.Events.OfType<UsageReportedEvent>());
        Assert.Equal(6, usage.InputTokens);
        Assert.Equal(866, usage.OutputTokens);
        Assert.Equal(61019, usage.CacheReadTokens);
        Assert.Equal(6701, usage.CacheWriteTokens);
        Assert.Equal(0.09490875m, usage.CostUsd);

        // The buckets are disjoint — 6 + 866 + 61019 + 6701 is exactly the totalTokens the
        // agent reported — which is why they need no subset correction and why totalTokens
        // is derivable rather than stored.
        Assert.Equal(
            68592,
            usage.InputTokens + usage.OutputTokens + usage.CacheReadTokens + usage.CacheWriteTokens);

        // Nothing in ACP attributes usage to a model, and guessing one from the profile's
        // argv would name a CLI rather than the model it routed to.
        Assert.Null(usage.Model);
    }

    /// <summary>
    /// An agent that prices a turn at exactly zero is one that does not compute cost, not one
    /// that ran for free — <c>opencode acp</c> sent this alongside 14,321 real tokens. §2
    /// principle 2: recording $0.00 would assert something untrue about the dispatch, so the
    /// tokens land and the cost stays absent.
    /// </summary>
    [Fact]
    public async Task An_explicit_zero_cost_is_recorded_as_no_cost_at_all()
    {
        var agent = new FakeAgent
        {
            DuringPrompt =
            [
                """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_1","update":{"sessionUpdate":"usage_update","used":14307,"size":200000,"cost":{"amount":0,"currency":"USD"}}}}""",
            ],
            TurnUsage =
            [
                """{"inputTokens":99,"outputTokens":14,"totalTokens":14321,"cachedReadTokens":14208}""",
            ],
        };
        var (_, drain) = Start(agent, Request("go"));
        var run = await drain;

        var usage = Assert.Single(run.Events.OfType<UsageReportedEvent>());
        Assert.Null(usage.CostUsd);
        Assert.Equal(99, usage.InputTokens);
        Assert.Equal(14, usage.OutputTokens);
        Assert.Equal(14208, usage.CacheReadTokens);
        Assert.Equal(0, usage.CacheWriteTokens); // the agent named no write bucket
    }

    /// <summary>
    /// Grok does not put buckets on PromptResponse.usage. Measured 2026-08-16: they
    /// arrive on <c>_x.ai/session_notification</c> / <c>response_completed</c> as
    /// snake_case, with no cost. The plane still has to record the tokens.
    /// </summary>
    [Fact]
    public async Task Grok_xai_usage_notification_reaches_the_plane_as_tokens_with_no_cost()
    {
        var agent = new FakeAgent
        {
            DuringPrompt =
            [
                """{"jsonrpc":"2.0","method":"_x.ai/session_notification","params":{"sessionId":"sess_1","update":{"sessionUpdate":"response_completed","usage":{"input_tokens":1048,"output_tokens":22,"cache_read_input_tokens":10240,"cache_creation_input_tokens":0,"reasoning_tokens":0}}}}""",
            ],
        };
        var (_, drain) = Start(agent, Request("go"));
        var run = await drain;

        var usage = Assert.Single(run.Events.OfType<UsageReportedEvent>());
        Assert.Null(usage.CostUsd);
        Assert.Null(usage.Model);
        Assert.Equal(1048, usage.InputTokens);
        Assert.Equal(22, usage.OutputTokens);
        Assert.Equal(10240, usage.CacheReadTokens);
        Assert.Equal(0, usage.CacheWriteTokens);
    }

    /// <summary>
    /// Each report is the session's running total, not the turn's own. That is a deliberate
    /// fit to the store: <c>TaskStore.RecordUsageAsync</c> keeps a high-water mark per bucket,
    /// so per-turn reports would leave the row holding the single largest turn instead of what
    /// the dispatch actually spent. Cumulative numbers make the max a no-op.
    /// </summary>
    [Fact]
    public async Task Usage_accumulates_across_the_turns_of_one_session()
    {
        var agent = new FakeAgent
        {
            HoldOpenAfterTurn = true,
            TurnUsage =
            [
                """{"inputTokens":10,"outputTokens":100,"cachedReadTokens":1000,"cachedWriteTokens":5}""",
                """{"inputTokens":20,"outputTokens":200,"cachedReadTokens":2000,"cachedWriteTokens":7}""",
            ],
        };
        var (client, drain) = Start(agent, Request("first"));

        await agent.WaitForTurnsAsync(1);
        client.TryQueueFollowUp();
        await agent.WaitForTurnsAsync(2);
        agent.EndSession();
        var run = await drain;

        var reports = run.Events.OfType<UsageReportedEvent>().ToList();
        Assert.Equal(2, reports.Count);
        Assert.Equal((10, 100, 1000, 5), Buckets(reports[0]));
        Assert.Equal((30, 300, 3000, 12), Buckets(reports[1]));

        static (long, long, long, long) Buckets(UsageReportedEvent u) =>
            (u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens);
    }

    /// <summary>
    /// An agent that reports neither buckets nor a cost produces no accounting line at all.
    /// <c>usage</c> on the prompt response is an unstable ACP feature and several agents omit
    /// it; a row of zeroes would claim a free dispatch, which is the same mislabelling as a
    /// $0.00 cost.
    /// </summary>
    [Fact]
    public async Task An_agent_that_reports_no_usage_produces_no_accounting_line()
    {
        var (_, drain) = Start(new FakeAgent(), Request("go"));
        var run = await drain;

        Assert.Empty(run.Events.OfType<UsageReportedEvent>());
        Assert.Single(run.Events.OfType<TurnEndedEvent>());
    }

    /// <summary>A queued follow-up waits for the turn in flight rather than racing it: two
    /// concurrent <c>session/prompt</c>s against an agent that has not declared prompt
    /// queueing are undefined, and the queue costs nothing.</summary>
    [Fact]
    public async Task A_follow_up_queued_mid_turn_waits_for_the_turn_in_flight()
    {
        var agent = new FakeAgent { HoldOpenAfterTurn = true, HoldPromptOpen = true };
        var (client, drain) = Start(agent, Request("first"));

        await agent.WaitForAsync("session/prompt");
        Assert.True(client.TryQueueFollowUp());

        // The first turn has not returned, so the second prompt must not have been sent.
        Assert.Single(agent.MethodsReceived, m => m == "session/prompt");

        agent.ReleasePrompt();
        await agent.WaitForTurnsAsync(2);
        agent.EndSession();
        await drain;

        Assert.Equal(["first", "Read your assignment again."], agent.PromptTexts);
    }

    /// <summary>A cancelled session takes no more messages: the conversation is over by
    /// request, and a follow-up accepted after it would be a Lead's message delivered into
    /// a session that is winding down.</summary>
    [Fact]
    public async Task A_cancelled_session_refuses_follow_ups()
    {
        var agent = new FakeAgent { HoldOpenAfterTurn = true };
        var (client, drain) = Start(agent, Request("go"));

        await agent.WaitForTurnsAsync(1);
        Assert.True(await client.CancelAsync(CancellationToken.None));
        Assert.False(client.TryQueueFollowUp());

        agent.EndSession();
        await drain;
    }

    /// <summary>And cancelling ends the wait rather than leaving the drive loop blocked on a
    /// queue nobody will write to again.</summary>
    [Fact]
    public async Task Cancelling_an_idle_session_ends_it_without_waiting_for_the_kill()
    {
        var agent = new FakeAgent { HoldOpenAfterTurn = true };
        var (client, drain) = Start(agent, Request("go"));

        await agent.WaitForTurnsAsync(1);
        await client.CancelAsync(CancellationToken.None);
        agent.EndSession();

        // Completes on the cancel path alone — no kill, no torn-down pipe.
        await drain.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // ── protocol version ────────────────────────────────────────────────────────

    /// <summary>
    /// Measured 2026-08-15: <c>claude-agent-acp</c> 0.68.0, <c>codex-acp</c> 1.3.0 and
    /// <c>opencode</c> 1.18.18 all answer <b>1</b>. So negotiating 1 is the normal case, and
    /// warning about it would put a line on every task of every ACP profile — training
    /// operators to scroll past the channel the real diagnostics use.
    /// </summary>
    [Fact]
    public async Task Negotiating_the_version_every_real_agent_speaks_is_not_a_warning()
    {
        var agent = new FakeAgent { ProtocolVersion = AcpClient.OldestProtocolVersion };
        var run = await RunAsync(agent, Request("go"));

        Assert.Equal(AcpClient.OldestProtocolVersion, run.Client.NegotiatedProtocolVersion);
        Assert.DoesNotContain(run.Warnings, w => w.Contains("protocol version"));
    }

    /// <summary>A version outside the range this client can hold a session over is worth saying.</summary>
    [Fact]
    public async Task Negotiating_a_version_outside_the_supported_range_is_a_warning()
    {
        var agent = new FakeAgent { ProtocolVersion = 99 };
        var run = await RunAsync(agent, Request("go"));

        Assert.Contains(run.Warnings, w => w.Contains("protocol version 99"));
    }

    // ── §11 resume ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Resume without a respawn: same process, same handshake, <c>session/load</c> instead
    /// of <c>session/new</c>. Note it carries the same <c>cwd</c> and <c>mcpServers</c> — the
    /// spec requires them to match the original session — which is why the request is built
    /// from the same fields either way.
    /// </summary>
    [Fact]
    public async Task Resumes_with_session_load_when_the_agent_supports_it()
    {
        var agent = new FakeAgent { LoadSession = true };
        var run = await RunAsync(agent, Request("go") with { ResumeSessionRef = "sess_old" });

        Assert.Equal(["initialize", "session/load", "session/prompt"], agent.MethodsReceived);
        var p = agent.Received("session/load").GetProperty("params");
        Assert.Equal("sess_old", p.GetProperty("sessionId").GetString());
        Assert.Equal("/work/task-1", p.GetProperty("cwd").GetString());
        Assert.True(p.TryGetProperty("mcpServers", out _));

        // A load is a wake: the follow-up, not the opening brief. Re-sending "go"
        // after load is what made the park bar re-ask the question it had already asked.
        var prompt = agent.Received("session/prompt").GetProperty("params")
            .GetProperty("prompt").EnumerateArray().Single();
        Assert.Equal("Read your assignment again.", prompt.GetProperty("text").GetString());

        // The resumed ref is the session, so it is what the plane hears about.
        Assert.Equal("sess_old", run.SessionId);
    }

    /// <summary>
    /// The capability is false by default, and an agent that does not declare it cannot
    /// reload a transcript. That is a cold start, and it is said out loud: a resume that
    /// silently became a cold start is precisely the §11 failure the stream mode's
    /// silent-stream warning exists to prevent, and the protocol change does not earn an
    /// exemption from it.
    /// </summary>
    [Fact]
    public async Task Cold_starts_and_warns_when_the_agent_cannot_load_a_session()
    {
        var agent = new FakeAgent { LoadSession = false };
        var run = await RunAsync(agent, Request("go") with { ResumeSessionRef = "sess_old" });

        Assert.Equal(["initialize", "session/new", "session/prompt"], agent.MethodsReceived);
        Assert.Contains(run.Warnings, w => w.Contains("loadSession") && w.Contains("COLD START"));
    }

    /// <summary>
    /// The plane's only channel to the worker is MCP (§5), so an agent that cannot be handed
    /// an HTTP MCP server is an agent that cannot call <c>get_task</c> or
    /// <c>report_result</c>. It will run and report nothing, which reads as a lazy model
    /// rather than a wiring fault unless the machine says so.
    /// </summary>
    [Fact]
    public async Task Warns_when_the_agent_cannot_take_an_http_mcp_server()
    {
        var agent = new FakeAgent { HttpMcp = false };
        var run = await RunAsync(agent, Request("go"));

        Assert.Contains(run.Warnings, w => w.Contains("mcpCapabilities.http") && w.Contains("NO docket tools"));
    }

    // ── stop ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// §10/§11: a stop is a real cancel here. Every stream profile in this repo is forced to
    /// <c>stop.mode: signal</c> because no harness reads a mid-task turn; ACP specifies one
    /// the agent must honour, so the cooperative wind-down stops being theoretical.
    /// </summary>
    [Fact]
    public async Task Cancel_sends_session_cancel_for_the_open_session()
    {
        var agent = new FakeAgent { HoldPromptOpen = true };
        var (client, drain) = Start(agent, Request("go"));

        await agent.WaitForAsync("session/prompt");
        Assert.True(await client.CancelAsync(CancellationToken.None));

        var cancel = agent.Received("session/cancel");
        Assert.Equal("sess_1", cancel.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.False(cancel.TryGetProperty("id", out _)); // a notification: no id, no ack

        agent.ReleasePrompt();
        await drain;
    }

    /// <summary>
    /// A second cancel is the deadline's business, not the protocol's. Sending another would
    /// be noise on a connection the agent is already winding down.
    /// </summary>
    [Fact]
    public async Task Cancel_is_sent_at_most_once()
    {
        var agent = new FakeAgent { HoldPromptOpen = true };
        var (client, drain) = Start(agent, Request("go"));

        await agent.WaitForAsync("session/prompt");
        Assert.True(await client.CancelAsync(CancellationToken.None));
        Assert.False(await client.CancelAsync(CancellationToken.None));

        Assert.Single(agent.MethodsReceived, m => m == "session/cancel");

        agent.ReleasePrompt();
        await drain;
    }

    // ── robustness ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The transport forbids non-ACP bytes on stdout, but a banner or a panic trace still
    /// happens, and the drain is what keeps the worker's pipe from filling — so a bad line
    /// must never end it. Same contract the terminal reader holds itself to.
    /// </summary>
    [Fact]
    public async Task A_non_json_stdout_line_does_not_end_the_drain()
    {
        var agent = new FakeAgent { NoiseBeforeResponses = "warning: something on stdout" };
        var run = await RunAsync(agent, Request("go"));

        Assert.Equal("sess_1", run.SessionId);
        Assert.Equal("end_turn", run.Client.StopReason);
    }

    /// <summary>
    /// The ACP counterpart of the silent-stream warning, diagnosing the mistake this mode
    /// actually invites: a profile whose spawn argv starts the harness's ordinary run
    /// command rather than its ACP server. The symptom is identical to a dozen other
    /// failures — a worker that starts and does nothing — so the machine names the cause.
    /// </summary>
    [Fact]
    public async Task Reports_a_worker_that_never_spoke_acp()
    {
        var agent = new FakeAgent { Mute = true };
        var run = await RunAsync(agent, Request("go"));

        Assert.Contains(run.Warnings, w => w.Contains("no JSON-RPC message at all"));
        Assert.Null(run.SessionId);
    }

    /// <summary>
    /// An agent that errors the handshake is reported as a handshake failure, not left to
    /// look like a model that did no work. The distinction matters because the remedies are
    /// completely different — one is a profile bug, the other is a prompt problem.
    /// </summary>
    [Fact]
    public async Task Reports_a_handshake_the_agent_refused()
    {
        var agent = new FakeAgent { FailInitialize = true };
        var run = await RunAsync(agent, Request("go"));

        Assert.Contains(run.Warnings, w => w.Contains("ACP handshake failed"));
        Assert.Null(run.SessionId);
    }

    // ── the generated MCP config translation ────────────────────────────────────

    /// <summary>
    /// §13: a transliteration, not an interpretation — the plane's document decides the
    /// server, the URL and the headers, and this only respells them.
    /// </summary>
    [Fact]
    public void Translates_the_generated_mcp_config_into_acp_servers()
    {
        var servers = AcpMcpServers.FromGeneratedConfig(GeneratedMcpConfig);

        var server = Assert.Single(servers);
        Assert.Equal("docket", server.Name);
        Assert.Equal("https://plane.example/mcp", server.Url);
        Assert.Equal(
            new KeyValuePair<string, string>("Authorization", "Bearer dkt_w_token"),
            Assert.Single(server.Headers));
    }

    /// <summary>
    /// A missing or malformed document yields no servers rather than throwing: by the time
    /// this runs the worker is already spawned, and the client's toolless-session warning
    /// diagnoses it far better than a spawn-time crash would.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"mcpServers":{}}""")]
    [InlineData("""{"mcpServers":{"stdio-only":{"command":"/bin/thing"}}}""")]
    public void Translates_an_unusable_mcp_config_into_no_servers(string? json) =>
        Assert.Empty(AcpMcpServers.FromGeneratedConfig(json));

    // ── harness ─────────────────────────────────────────────────────────────────

    private const string GeneratedMcpConfig =
        """
        {"mcpServers":{"docket":{"type":"http","url":"https://plane.example/mcp",
        "headers":{"Authorization":"Bearer dkt_w_token"}}}}
        """;

    private static AcpSessionRequest Request(string prompt, string followUp = "Read your assignment again.") =>
        new("/work/task-1", prompt, followUp, AcpMcpServers.FromGeneratedConfig(GeneratedMcpConfig));

    // These fixtures are written as literal JSON with placeholders rather than as
    // interpolated raw strings: ACP payloads nest three and four braces deep, which fights
    // the `$$"""` delimiters for no benefit. Substitution keeps the fixture readable as the
    // wire text it is meant to be.
    private static string ToolCall(string id, string kindOfUpdate, string? title, string kind, string status) =>
        """
        {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_1","update":{"sessionUpdate":"%update%","toolCallId":"%id%",%title%"kind":"%kind%","status":"%status%"}}}
        """
            .Replace("%update%", kindOfUpdate)
            .Replace("%id%", id)
            .Replace("%title%", title is null ? "" : $"\"title\":{JsonSerializer.Serialize(title)},")
            .Replace("%kind%", kind)
            .Replace("%status%", status);

    private static (AcpClient Client, Task<Run> Drain) Start(
        FakeAgent agent,
        AcpSessionRequest request,
        Func<AcpPermissionAsk, CancellationToken, Task<AcpPermissionDecision>>? requestPermission = null)
    {
        var ring = new OutboundEventRing(capacity: 256);
        string? session = null;
        var warnings = new List<string>();

        var client = new AcpClient(
            Task1,
            ring,
            new FakeTimeProvider(),
            request,
            onSessionId: id => session = id,
            warn: warnings.Add,
            requestPermission: requestPermission);

        agent.Bind(client);

        var drain = System.Threading.Tasks.Task.Run(async () =>
        {
            await client.RunAsync(agent.AgentToClient, agent.ClientToAgent, CancellationToken.None);

            ring.Complete();
            var events = new List<RunnerEvent>();
            await foreach (var item in ring.ReadAllAsync(CancellationToken.None))
                events.Add(item.Event);

            return new Run(client, events, session, warnings);
        });

        return (client, drain);
    }

    private static async Task<Run> RunAsync(
        FakeAgent agent,
        AcpSessionRequest request,
        Func<AcpPermissionAsk, CancellationToken, Task<AcpPermissionDecision>>? requestPermission = null)
    {
        var (_, drain) = Start(agent, request, requestPermission);
        return await drain.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private sealed record Run(
        AcpClient Client, List<RunnerEvent> Events, string? SessionId, List<string> Warnings)
    {
        public List<string> ToolNames => Events.OfType<ToolCallEvent>().Select(e => e.Tool).ToList();
    }

    /// <summary>
    /// A scripted ACP agent on the other end of the two pipes: it reads what the client
    /// writes, answers the way the spec says an agent must, and can be configured to answer
    /// badly. Deliberately hand-rolled rather than built on an ACP SDK — a fake that shares
    /// an implementation with the code under test would agree with it about anything they
    /// both got wrong.
    /// </summary>
    private sealed class FakeAgent
    {
        private readonly Channel<string> _toClient = Channel.CreateUnbounded<string>();
        private readonly List<JsonElement> _received = [];
        private readonly Channel<string> _methodSeen = Channel.CreateUnbounded<string>();
        private readonly TaskCompletionSource _promptHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _gate = new();
        private readonly Channel<int> _turnsSeen = Channel.CreateUnbounded<int>();
        private int _turns;

        public bool LoadSession { get; init; }
        public bool HttpMcp { get; init; } = true;
        public int ProtocolVersion { get; init; } = AcpClient.LatestProtocolVersion;
        public bool FailInitialize { get; init; }
        public bool Mute { get; init; }
        public bool HoldPromptOpen { get; init; }

        /// <summary>Keep the session open after a turn ends, the way a real ACP agent does —
        /// it is a server, not a one-shot. Without this the fake completes its stdout after
        /// the first turn, which is the old task-model shape.</summary>
        public bool HoldOpenAfterTurn { get; init; }

        /// <summary>Stop reason per turn, in order; the last repeats once exhausted.</summary>
        public IReadOnlyList<string> StopReasons { get; init; } = ["end_turn"];

        /// <summary>The <c>usage</c> object to hang off each turn's <c>PromptResponse</c>, as
        /// raw JSON; a null entry omits it entirely, which is what an agent that does not
        /// implement the feature sends. The last entry repeats once exhausted.</summary>
        public IReadOnlyList<string?> TurnUsage { get; init; } = [null];
        /// <summary>Method ids to declare at <c>initialize</c>, in order — the only preference
        /// signal ACP gives. Empty is what claude-agent-acp sends.</summary>
        public IReadOnlyList<string> AuthMethods { get; init; } = [];

        /// <summary>Refuse <c>session/new</c> with ACP's auth-required code until
        /// <c>authenticate</c> has run, the way codex-acp 1.3.0 does.</summary>
        public bool RequireAuthentication { get; init; }

        /// <summary>Reject <c>authenticate</c> itself — a credential that is wrong rather
        /// than missing.</summary>
        public bool AuthenticateFails { get; init; }

        /// <summary>The method id the client chose, or null if it never authenticated.</summary>
        public string? AuthenticatedWith { get; private set; }

        /// <summary>
        /// Values advertised on <c>session/new</c> as select <c>configOptions</c>.
        /// Empty is the agent that offers none.
        /// </summary>
        public IReadOnlyDictionary<string, string> AdvertiseOptions { get; init; } =
            new Dictionary<string, string>();

        /// <summary>Values the client pinned via <c>session/set_config_option</c>.</summary>
        public Dictionary<string, string> PinnedOptions { get; } = new(StringComparer.Ordinal);

        private string AuthMethodsJson =>
            "[" + string.Join(",", AuthMethods.Select(m => $$"""{"id":"{{m}}","name":"{{m}}"}""")) + "]";

        private string ConfigOptionsJson()
        {
            var parts = new List<string>();
            foreach (var (id, advertised) in AdvertiseOptions)
            {
                var current = PinnedOptions.GetValueOrDefault(id, advertised);
                parts.Add(
                    $$"""{"id":"{{id}}","type":"select","currentValue":"{{current}}","options":[{"value":"{{advertised}}","name":"{{id}}"}]}""");
            }

            return "[" + string.Join(",", parts) + "]";
        }

        public bool AskPermissionDuringPrompt { get; init; }
        public string PermissionOptions { get; init; } =
            """[{"optionId":"allow-once","name":"Allow once","kind":"allow_once"},{"optionId":"allow-always","name":"Always allow","kind":"allow_always"}]""";
        public string? RequestDuringPrompt { get; init; }
        public string? NoiseBeforeResponses { get; init; }
        public IReadOnlyList<string> DuringPrompt { get; init; } = [];

        public TextReader AgentToClient { get; }
        public TextWriter ClientToAgent { get; }

        public JsonElement? PermissionResponse { get; private set; }

        /// <summary>The text of every session/prompt received, in order.</summary>
        public List<string> PromptTexts
        {
            get
            {
                lock (_gate)
                    return _received
                        .Where(m => m.TryGetProperty("method", out var x) && x.GetString() == "session/prompt")
                        .Select(m => m.GetProperty("params").GetProperty("prompt")
                            .EnumerateArray().First().GetProperty("text").GetString() ?? "")
                        .ToList();
            }
        }
        public JsonElement? UnsupportedResponse { get; private set; }

        public FakeAgent()
        {
            AgentToClient = new ChannelLineReader(_toClient);
            ClientToAgent = new LineWriter(OnClientLine);
        }

        public List<string> MethodsReceived
        {
            get
            {
                lock (_gate)
                    return _received
                        .Select(m => m.TryGetProperty("method", out var x) ? x.GetString() ?? "" : "")
                        .Where(m => m.Length > 0)
                        .ToList();
            }
        }

        public JsonElement Received(string method)
        {
            lock (_gate)
                return _received.First(m =>
                    m.TryGetProperty("method", out var x) && x.GetString() == method);
        }

        /// <summary>Blocks until the client has sent <paramref name="method"/>.</summary>
        public async Task WaitForAsync(string method)
        {
            while (true)
            {
                var seen = await _methodSeen.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
                if (seen == method)
                    return;
            }
        }

        public void Bind(AcpClient client) => _ = client;

        /// <summary>Lets a held prompt turn finish, for the cancel tests.</summary>
        public void ReleasePrompt() => _promptHeld.TrySetResult();

        private void OnClientLine(string line)
        {
            if (line.Trim().Length == 0)
                return;

            var message = JsonDocument.Parse(line).RootElement.Clone();
            lock (_gate)
                _received.Add(message);

            var method = message.TryGetProperty("method", out var m) ? m.GetString() : null;

            // A response from the client to something we asked. Recorded for assertions;
            // nothing further to drive from it.
            if (method is null)
            {
                if (message.TryGetProperty("error", out _))
                    UnsupportedResponse = message;
                else
                    PermissionResponse = message;
                return;
            }

            _methodSeen.Writer.TryWrite(method);

            // A worker that speaks no ACP still exits eventually, and its stdout EOF is what
            // ends the client's read loop. Completing here is that exit — without it the
            // fake would hang where a real process would not, and the test would be
            // asserting against a condition the runner never actually sees.
            if (Mute)
            {
                _toClient.Writer.TryComplete();
                return;
            }

            var id = message.TryGetProperty("id", out var i) ? i.GetRawText() : null;

            switch (method)
            {
                case "initialize":
                    Noise();
                    Send(FailInitialize
                        ? """{"jsonrpc":"2.0","id":%id%,"error":{"code":-32603,"message":"no"}}"""
                        : """
                          {"jsonrpc":"2.0","id":%id%,"result":{"protocolVersion":%version%,"agentCapabilities":{"loadSession":%load%,"mcpCapabilities":{"http":%http%}},"authMethods":%auth%,"agentInfo":{"name":"fake","version":"1"}}}
                          """
                            .Replace("%version%", ProtocolVersion.ToString())
                            .Replace("%load%", Lower(LoadSession))
                            .Replace("%http%", Lower(HttpMcp))
                            .Replace("%auth%", AuthMethodsJson),
                        id);
                    // An agent that refuses the handshake has nothing further to say and
                    // exits; the EOF is what the client sees next.
                    if (FailInitialize)
                        _toClient.Writer.TryComplete();
                    break;

                case "authenticate":
                    AuthenticatedWith = message.GetProperty("params").GetProperty("methodId").GetString();
                    Send(AuthenticateFails
                        ? """{"jsonrpc":"2.0","id":%id%,"error":{"code":-32000,"message":"bad credential"}}"""
                        : """{"jsonrpc":"2.0","id":%id%,"result":{}}""",
                        id);
                    // A rejected credential leaves the agent with nothing to do; it exits and
                    // the EOF is what the client sees next, exactly as with FailInitialize.
                    if (AuthenticateFails)
                        _toClient.Writer.TryComplete();
                    break;

                case "session/new":
                    if (RequireAuthentication && AuthenticatedWith is null)
                    {
                        Send(
                            """{"jsonrpc":"2.0","id":%id%,"error":{"code":-32000,"message":"Authentication required"}}""",
                            id);
                        break;
                    }
                    Send(
                        AdvertiseOptions.Count > 0
                            ? """{"jsonrpc":"2.0","id":%id%,"result":{"sessionId":"sess_1","configOptions":%opts%}}"""
                                .Replace("%opts%", ConfigOptionsJson())
                            : """{"jsonrpc":"2.0","id":%id%,"result":{"sessionId":"sess_1"}}""",
                        id);
                    break;

                case "session/set_config_option":
                    var p = message.GetProperty("params");
                    var configId = p.GetProperty("configId").GetString() ?? "";
                    var value = p.GetProperty("value").GetString() ?? "";
                    PinnedOptions[configId] = value;
                    Send(
                        """{"jsonrpc":"2.0","id":%id%,"result":{"configOptions":%opts%}}"""
                            .Replace("%opts%", ConfigOptionsJson()),
                        id);
                    break;

                case "session/load":
                    // The spec has the agent replay the conversation before answering; those
                    // replayed updates ride the same read loop as live ones, which is what
                    // the toolCallId guard has to survive.
                    foreach (var update in DuringPrompt)
                        Send(update);
                    Send("""{"jsonrpc":"2.0","id":%id%,"result":{}}""", id);
                    break;

                case "session/prompt":
                    _ = RespondToPromptAsync(id);
                    break;

                case "session/cancel":
                    break;
            }
        }

        private async Task RespondToPromptAsync(string? id)
        {
            foreach (var update in DuringPrompt)
                Send(update);

            if (AskPermissionDuringPrompt)
                Send(
                    """{"jsonrpc":"2.0","id":9001,"method":"session/request_permission","params":{"sessionId":"sess_1","title":"Approve?","options":%options%}}"""
                        .Replace("%options%", PermissionOptions));

            if (RequestDuringPrompt is { } unsupported)
                Send(
                    """{"jsonrpc":"2.0","id":9002,"method":"%method%","params":{"path":"/etc/hosts"}}"""
                        .Replace("%method%", unsupported));

            if (HoldPromptOpen)
                await _promptHeld.Task;

            var turn = Interlocked.Increment(ref _turns);
            var reason = StopReasons[Math.Min(turn - 1, StopReasons.Count - 1)];
            var usage = TurnUsage[Math.Min(turn - 1, TurnUsage.Count - 1)];
            Send(
                """{"jsonrpc":"2.0","id":%id%,"result":{"stopReason":"%reason%"%usage%}}"""
                    .Replace("%reason%", reason)
                    .Replace("%usage%", usage is null ? "" : ",\"usage\":" + usage),
                id);
            _turnsSeen.Writer.TryWrite(turn);

            // A real ACP agent is a server: answering a prompt does not end it, and its
            // stdout stays open for the next one. HoldOpenAfterTurn models that. Without it
            // the fake ends its stdout here, which is the old one-turn-per-process shape and
            // is what the single-turn tests above still exercise.
            if (!HoldOpenAfterTurn)
                _toClient.Writer.TryComplete();
        }

        /// <summary>Blocks until the agent has completed <paramref name="count"/> turns.</summary>
        public async Task WaitForTurnsAsync(int count)
        {
            while (Volatile.Read(ref _turns) < count)
                await _turnsSeen.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
        }

        /// <summary>Ends the agent's stdout, the way its process exiting would.</summary>
        public void EndSession() => _toClient.Writer.TryComplete();

        private void Noise()
        {
            if (NoiseBeforeResponses is { } noise)
                _toClient.Writer.TryWrite(noise);
        }

        private void Send(string line, string? id = null) =>
            _toClient.Writer.TryWrite(
                (id is null ? line : line.Replace("%id%", id)).ReplaceLineEndings(""));

        private static string Lower(bool value) => value ? "true" : "false";
    }

    /// <summary>The agent's stdout as the client sees it: one JSON-RPC message per line.</summary>
    private sealed class ChannelLineReader(Channel<string> lines) : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken ct)
        {
            try
            {
                return await lines.Reader.ReadAsync(ct);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The agent's stdin as the client writes it. Splits on the newline the transport uses
    /// as its frame delimiter — which is a real assertion in itself, since a client that
    /// embedded a newline in a message would show up here as two unparseable halves.
    /// </summary>
    private sealed class LineWriter(Action<string> onLine) : TextWriter
    {
        private readonly StringBuilder _buffer = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                var line = _buffer.ToString();
                _buffer.Clear();
                onLine(line);
                return;
            }

            _buffer.Append(value);
        }

        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken ct = default)
        {
            foreach (var c in buffer.Span)
                Write(c);
            return System.Threading.Tasks.Task.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;
    }
}
