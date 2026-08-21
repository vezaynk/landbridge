using System.Text.Json;
using Landbridge.Contracts;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Landbridge.Mcp.Auth;
using Landbridge.Mcp.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// §11's permission bridge over a Postgres-backed store: the worker's relaying tool, the
/// Lead's triage, the escalation that hands one request to a human, and the audit rows all
/// three leave behind.
///
/// <para>These tests use <see cref="TimeProvider.System"/> rather than the fake clock the
/// rest of this suite prefers, because the thing under test is a <em>live</em> wait — the
/// worker's tool call really does block until someone answers. A fake clock would have to be
/// advanced from inside the delay it is measuring. The wait is instead made cheap
/// (<see cref="PollMs"/>) and every test that blocks does so on a background task the answer
/// releases, so nothing here sleeps for a fixed duration.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PermissionBridgeTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly TeamId Team = TeamId.New();

    /// <summary>Short enough that a blocked call notices its verdict promptly.</summary>
    private const int PollMs = 10;

    /// <summary>A ceiling on every wait, so a bug fails the test instead of hanging the suite.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private const string Tool = "Bash";
    private const string ProposedInput = """{"command":"git ls-remote origin"}""";

    private static IHttpContextAccessor AccessorFor(Principal principal) =>
        new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = LandbridgeClaims.ToClaimsPrincipal(principal) },
        };

    private LeadTools LeadFor(Principal principal, RunnerConnectionRegistry? registry = null) =>
        RelayGrantTestKit.LeadToolsFor(
            pg.NewContext(), TimeProvider.System,
            registry ?? new RunnerConnectionRegistry(TimeProvider.System), AccessorFor(principal));

    private WorkerTools WorkerFor(WorkerCaller caller) =>
        RelayGrantTestKit.WorkerToolsFor(
            pg.NewContext(), TimeProvider.System, new RunnerConnectionRegistry(TimeProvider.System),
            AccessorFor(new Principal.Worker(caller)),
            PollMs);

    private static MachineSnapshot Machine() =>
        new("m1", Ready: true, UnderBackPressure: false, new HashSet<string> { "default" });

    /// <summary>A dispatched, working task and the credential of its incumbent worker.</summary>
    private async Task<WorkerCaller> SeedWorkingTask()
    {
        await using var db = pg.NewContext();
        var store = new SessionStore(db, TimeProvider.System);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateSession(new LeadClaim(Team), Team, "needs permission", "default"));
        var instance = WorkerInstanceId.New();
        var dispatched = await store.DispatchNextAsync(Machine(), instance);
        Assert.IsType<StoreResult.Applied>(dispatched);
        return new WorkerCaller(Team, created.Session.Id, instance);
    }

    private async Task EscalateAsync(SessionId session, string reason)
    {
        await using var db = pg.NewContext();
        Assert.IsType<StoreResult.Applied>(
            await new SessionStore(db, TimeProvider.System).EscalatePermissionAsync(
                new LeadClaim(Team), session, reason));
    }

    /// <summary>
    /// Starts the worker's relaying call and waits until the request has actually landed in
    /// blocked_on_input, so a test answers a request that exists rather than racing it.
    /// </summary>
    private async Task<Task<string>> AskPermissionAsync(
        WorkerCaller caller, string tool = Tool, string input = ProposedInput)
    {
        var worker = WorkerFor(caller);
        var pending = Task.Run(() => worker.RequestPermission(
            tool, JsonDocument.Parse(input).RootElement, "toolu_test", CancellationToken.None));
        await WaitForAsync(async () => await StateOf(caller.Session) == SessionState.BlockedOnInput, pending);
        return pending;
    }

    private async Task<SessionState> StateOf(SessionId task)
    {
        await using var db = pg.NewContext();
        return await db.Sessions.AsNoTracking().Where(t => t.Id == task.Value)
            .Select(t => t.State).SingleAsync();
    }

    /// <summary>
    /// Polls <paramref name="condition"/> until it holds, failing if the blocking call under
    /// test finished first (which would mean it never really waited) or if patience runs out.
    /// </summary>
    private static async Task WaitForAsync(Func<Task<bool>> condition, Task? mustStillBeRunning = null)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            if (mustStillBeRunning is { IsCompleted: true })
                Assert.Fail($"the call under test returned before the condition held: {await ResultOf(mustStillBeRunning)}");
            await Task.Delay(PollMs);
        }
        Assert.Fail("timed out waiting for the condition to hold");
    }

    private static async Task<string> ResultOf(Task task) =>
        task is Task<string> t ? await t : "<not a string result>";

    private static JsonElement Verdict(string json) => JsonDocument.Parse(json).RootElement;

    [SkippableFact]
    public async Task Protocol_tools_auto_allow_without_blocking()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var caller = await SeedWorkingTask();
        var worker = WorkerFor(caller);

        var json = await worker.RequestPermission(
            "mcp__landbridge__get_inbox",
            JsonDocument.Parse("{}").RootElement,
            "toolu_proto",
            CancellationToken.None);

        Assert.Contains("\"behavior\":\"allow\"", json, StringComparison.Ordinal);
        Assert.Equal(SessionState.Working, await StateOf(caller.Session));
    }

    [SkippableFact]
    public async Task Credential_tools_still_ask()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var caller = await SeedWorkingTask();

        var pending = await AskPermissionAsync(
            caller,
            "Bash",
            """{"command":"/usr/bin/security find-generic-password -s prod"}""");

        Assert.Equal(SessionState.BlockedOnInput, await StateOf(caller.Session));
        Assert.False(pending.IsCompleted);
    }

    // ── The round trip ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_permission_request_blocks_the_task_and_records_the_tool_and_input()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var caller = await SeedWorkingTask();

        var pending = await AskPermissionAsync(caller);

        await using var db = pg.NewContext();
        var row = await db.Sessions.AsNoTracking().SingleAsync(t => t.Id == caller.Session.Value);
        Assert.Equal(SessionState.BlockedOnInput, row.State);
        Assert.Equal(InputRequestKind.Permission, row.InputKind);
        Assert.Equal(Tool, row.PermissionTool);
        // The proposed input is stored verbatim — not reformatted, not parsed.
        Assert.Equal(ProposedInput, row.InputQuestion);
        Assert.Null(row.PermissionVerdict);
        // Still blocked: the worker's own process is holding the call open, which is what
        // distinguishes this kind from every other.
        Assert.False(pending.IsCompleted);
        Assert.NotNull(row.CurrentInstanceId);

        // Release the worker so the test does not leave a task blocking on nothing.
        await LeadFor(new Principal.Lead(Team))
            .AnswerPermissionRequest(caller.Session.ToString(), "allow", null, CancellationToken.None);
        await pending.WaitAsync(Patience);
    }

    [SkippableFact]
    public async Task A_lead_allow_unblocks_the_worker_with_the_harness_allow_shape()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var caller = await SeedWorkingTask();
        var pending = await AskPermissionAsync(caller);

        await LeadFor(new Principal.Lead(Team))
            .AnswerPermissionRequest(caller.Session.ToString(), "allow", null, CancellationToken.None);

        // The contract verified on the wire against Claude Code 2.1.220: a PermissionResult
        // with behavior 'allow', carrying the proposed input back unchanged.
        var verdict = Verdict(await pending.WaitAsync(Patience));
        Assert.Equal("allow", verdict.GetProperty("behavior").GetString());
        Assert.Equal(
            JsonDocument.Parse(ProposedInput).RootElement.GetRawText(),
            verdict.GetProperty("updatedInput").GetRawText());

        // And the worker is working again on its own instance — never requeued, so its
        // token still resolves and there is no successor.
        await using var db = pg.NewContext();
        var row = await db.Sessions.AsNoTracking().SingleAsync(t => t.Id == caller.Session.Value);
        Assert.Equal(SessionState.Working, row.State);
        Assert.Equal(caller.Instance.Value, row.CurrentInstanceId);
        Assert.Null(row.ParkMachine);
        Assert.Null(row.BlockedAt);
        Assert.False(await db.WorkerInstances.AsNoTracking()
            .Where(w => w.Id == caller.Instance.Value).Select(w => w.Revoked).SingleAsync());
    }

    [SkippableFact]
    public async Task A_lead_deny_reaches_the_worker_as_guidance_it_can_read()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        const string guidance = "no keychain access on this task; read the fixture at tests/fixtures/creds.json instead";
        var caller = await SeedWorkingTask();
        var pending = await AskPermissionAsync(caller);

        await LeadFor(new Principal.Lead(Team))
            .AnswerPermissionRequest(caller.Session.ToString(), "deny", guidance, CancellationToken.None);

        var verdict = Verdict(await pending.WaitAsync(Patience));
        Assert.Equal("deny", verdict.GetProperty("behavior").GetString());
        // The message is the point of a denial: it is what the agent adapts to.
        Assert.Equal(guidance, verdict.GetProperty("message").GetString());
        // Denied is not requeued — the worker keeps going and decides what to do next.
        Assert.Equal(SessionState.Working, await StateOf(caller.Session));
    }

    [SkippableFact]
    public async Task A_deny_without_a_message_is_refused_and_the_worker_stays_blocked()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var caller = await SeedWorkingTask();
        var pending = await AskPermissionAsync(caller);
        var lead = LeadFor(new Principal.Lead(Team));

        var refused = await Assert.ThrowsAsync<McpException>(
            () => lead.AnswerPermissionRequest(caller.Session.ToString(), "deny", null, CancellationToken.None));
        Assert.Contains(Rule.PermissionDenialCarriesMessage.ToString(), refused.Message);

        Assert.Equal(SessionState.BlockedOnInput, await StateOf(caller.Session));
        Assert.False(pending.IsCompleted);

        await lead.AnswerPermissionRequest(caller.Session.ToString(), "deny", "no, use the fixture", CancellationToken.None);
        await pending.WaitAsync(Patience);
    }

    [SkippableFact]
    public async Task An_unknown_option_is_refused()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var caller = await SeedWorkingTask();
        var pending = await AskPermissionAsync(caller);
        var lead = LeadFor(new Principal.Lead(Team));

        var refused = await Assert.ThrowsAsync<McpException>(
            () => lead.AnswerPermissionRequest(caller.Session.ToString(), "maybe", null, CancellationToken.None));
        Assert.Contains("allow", refused.Message);

        await lead.AnswerPermissionRequest(caller.Session.ToString(), "allow", null, CancellationToken.None);
        await pending.WaitAsync(Patience);
    }

    // ── The two answer paths refuse each other's requests ─────────────────────

    [SkippableFact]
    public async Task Prose_cannot_answer_a_permission_request()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var caller = await SeedWorkingTask();
        var pending = await AskPermissionAsync(caller);
        var lead = LeadFor(new Principal.Lead(Team));

        // answer_input_request would revoke this worker's token and requeue the task, which
        // would strand a process still holding its tool call open.
        var refused = await Assert.ThrowsAsync<McpException>(
            () => lead.SendInputResponse(caller.Session.ToString(), "go ahead", CancellationToken.None));
        Assert.Contains(Rule.PermissionVerdictAnswersPermissionRequests.ToString(), refused.Message);

        Assert.Equal(SessionState.BlockedOnInput, await StateOf(caller.Session));
        Assert.False(pending.IsCompleted);

        await lead.AnswerPermissionRequest(caller.Session.ToString(), "allow", null, CancellationToken.None);
        await pending.WaitAsync(Patience);
    }

    [SkippableFact]
    public async Task A_verdict_cannot_answer_an_ordinary_question()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var caller = await SeedWorkingTask();
        await using (var db = pg.NewContext())
        {
            await new SessionStore(db, TimeProvider.System).ApplyAsync(
                caller.Session, new RequestInput(caller, InputRequestKind.Question, "which database?"));
        }

        var refused = await Assert.ThrowsAsync<McpException>(
            () => LeadFor(new Principal.Lead(Team))
                .AnswerPermissionRequest(caller.Session.ToString(), "allow", null, CancellationToken.None));

        Assert.Contains(Rule.PermissionVerdictAnswersPermissionRequests.ToString(), refused.Message);
        Assert.Equal(SessionState.Working, await StateOf(caller.Session));
    }

    // ── Escalation ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Escalation_removes_the_leads_authority_and_a_human_answers_instead()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        const string why = "this reads a production credential; the task description never mentions one";
        var caller = await SeedWorkingTask();
        var pending = await AskPermissionAsync(caller);
        var lead = LeadFor(new Principal.Lead(Team));

        await EscalateAsync(caller.Session, why);

        // Still blocked, still the same request — escalation is an authority change.
        Assert.Equal(SessionState.BlockedOnInput, await StateOf(caller.Session));
        Assert.False(pending.IsCompleted);

        var refused = await Assert.ThrowsAsync<McpException>(
            () => lead.AnswerPermissionRequest(caller.Session.ToString(), "allow", null, CancellationToken.None));
        Assert.Contains(Rule.EscalatedPermissionIsHumanOnly.ToString(), refused.Message);

        // The human is not blocked by the escalation, and never was.
        await using (var db = pg.NewContext())
        {
            var applied = await new SessionStore(db, TimeProvider.System).AnswerPermissionAsync(
                new HumanSession(), caller.Session, PermissionVerdict.Deny, "denied: use the CI secret store");
            Assert.IsType<StoreResult.Applied>(applied);
        }

        var verdict = Verdict(await pending.WaitAsync(Patience));
        Assert.Equal("deny", verdict.GetProperty("behavior").GetString());
        Assert.Equal("denied: use the CI secret store", verdict.GetProperty("message").GetString());

        await using var check = pg.NewContext();
        var row = await check.Sessions.AsNoTracking().SingleAsync(t => t.Id == caller.Session.Value);
        Assert.NotNull(row.PermissionEscalatedAt);
        Assert.Equal(why, row.PermissionEscalationReason);
    }

    [SkippableFact]
    public async Task Escalation_requires_a_reason()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var caller = await SeedWorkingTask();
        var pending = await AskPermissionAsync(caller);
        var lead = LeadFor(new Principal.Lead(Team));

        await using (var db = pg.NewContext())
        {
            var refused = Assert.IsType<StoreResult.Rejected>(
                await new SessionStore(db, TimeProvider.System).EscalatePermissionAsync(
                    new LeadClaim(Team), caller.Session, "  "));
            Assert.Equal(Rule.PermissionEscalationCarriesReason, refused.Rule);
        }

        // Not escalated, so the Lead still has authority.
        await lead.AnswerPermissionRequest(caller.Session.ToString(), "allow", null, CancellationToken.None);
        await pending.WaitAsync(Patience);
    }

    [SkippableFact]
    public async Task A_human_can_answer_a_request_nobody_escalated()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var caller = await SeedWorkingTask();
        var pending = await AskPermissionAsync(caller);

        await using (var db = pg.NewContext())
        {
            var applied = await new SessionStore(db, TimeProvider.System).AnswerPermissionAsync(
                new HumanSession(), caller.Session, PermissionVerdict.Allow);
            Assert.IsType<StoreResult.Applied>(applied);
        }

        Assert.Equal("allow", Verdict(await pending.WaitAsync(Patience)).GetProperty("behavior").GetString());
    }

    // ── Abandonment ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task An_unanswered_request_parks_and_the_worker_is_told_to_stop()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var caller = await SeedWorkingTask();
        var pending = await AskPermissionAsync(caller);

        // The sweeper's own path, with a TTL short enough that the wait it measures has
        // already elapsed. NOT zero: auto-park is off by default now (a session is held
        // until park_session, not until a timer), and zero is one of the two values that mean
        // "do not auto-park" — so a zero here would assert the opposite of the intent. A
        // live machine tracking the task is what lets it park rather than requeue.
        var registry = new RunnerConnectionRegistry(TimeProvider.System);
        registry.Register("m1", new HashSet<string> { "default" }, (_, _) => Task.CompletedTask);
        registry.ApplyHeartbeat("m1", new MachineHeartbeat(
            "m1", Ready: true, UnderBackPressure: false, new SystemLoad(0, 0, 0), RunningSessions: 0,
            ["default"], DateTimeOffset.UtcNow));
        registry.TrackDispatch("m1", caller.Session);
        var sweeper = new WaitTtlSweeper(
            ScopeFactory(), registry, TimeProvider.System, NullLogger<WaitTtlSweeper>.Instance,
            waitTtl: TimeSpan.FromMilliseconds(1), machineLivenessWindow: TimeSpan.FromHours(1),
            sweepInterval: null);

        await Task.Delay(5);
        await sweeper.SweepAsync(CancellationToken.None);
        // Permission waits occupy the seat; wait-TTL will not deactivate them.
        Assert.Equal(SessionState.BlockedOnInput, await StateOf(caller.Session));
        Assert.False(pending.IsCompleted);
    }

    [SkippableFact]
    public async Task A_second_concurrent_request_is_denied_rather_than_overwriting_the_first()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var caller = await SeedWorkingTask();
        var first = await AskPermissionAsync(caller);

        // The engine's state gate serializes prompts for free: a task already blocked is not
        // working, so a second request is refused — and the tool still owes the harness a
        // well-formed verdict rather than an error.
        var second = await WorkerFor(caller).RequestPermission(
            "Bash", JsonDocument.Parse("""{"command":"git status"}""").RootElement, "toolu_two",
            CancellationToken.None);

        var verdict = Verdict(second);
        Assert.Equal("deny", verdict.GetProperty("behavior").GetString());
        Assert.Contains("InvalidSourceState", verdict.GetProperty("message").GetString()!);

        // The first request is untouched: still Bash, still pending.
        await using (var db = pg.NewContext())
        {
            var row = await db.Sessions.AsNoTracking().SingleAsync(t => t.Id == caller.Session.Value);
            Assert.Equal(Tool, row.PermissionTool);
            Assert.Equal(ProposedInput, row.InputQuestion);
        }

        await LeadFor(new Principal.Lead(Team))
            .AnswerPermissionRequest(caller.Session.ToString(), "allow", null, CancellationToken.None);
        await first.WaitAsync(Patience);
    }

    // ── The Lead's read ───────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Get_task_question_renders_a_permission_request_as_untrusted_tool_and_input()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var caller = await SeedWorkingTask();
        var pending = await AskPermissionAsync(caller);
        var lead = LeadFor(new Principal.Lead(Team));

        var item = Assert.Single((await lead.GetLeadInbox(caller.Session.ToString(), CancellationToken.None)).Items);
        Assert.Equal(LeadInboxKind.Permission, item.Kind);
        Assert.Equal(Tool, item.PermissionTool);
        Assert.Equal(ProposedInput, item.Question);
        Assert.Null(item.PermissionOptions);
        Assert.Null(item.EscalationReason);

        await EscalateAsync(caller.Session, "credential access");
        var escalated = Assert.Single((await lead.GetLeadInbox(caller.Session.ToString(), CancellationToken.None)).Items);
        Assert.Equal("credential access", escalated.EscalationReason);

        await using (var db = pg.NewContext())
        {
            await new SessionStore(db, TimeProvider.System).AnswerPermissionAsync(
                new HumanSession(), caller.Session, PermissionVerdict.Allow, "fine, just this once");
        }
        await pending.WaitAsync(Patience);

        Assert.DoesNotContain(
            (await lead.GetLeadInbox(caller.Session.ToString(), CancellationToken.None)).Items,
            i => i.Kind == LeadInboxKind.Permission);
    }

    [SkippableFact]
    public async Task Get_session_question_lists_harness_options_and_the_lead_picks_one()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var caller = await SeedWorkingTask();
        const string options = """[{"optionId":"allow-once","name":"Allow once","kind":"allow_once"},{"optionId":"reject-once","name":"Reject","kind":"reject_once"}]""";
        await using (var db = pg.NewContext())
        {
            Assert.IsType<StoreResult.Applied>(await new SessionStore(db, TimeProvider.System).ApplyAsync(
                caller.Session,
                new RequestInput(caller, InputRequestKind.Permission, ProposedInput, Tool, options)));
        }

        var lead = LeadFor(new Principal.Lead(Team));
        var item = Assert.Single((await lead.GetLeadInbox(caller.Session.ToString(), CancellationToken.None)).Items);
        Assert.Equal(LeadInboxKind.Permission, item.Kind);
        Assert.NotNull(item.PermissionOptions);
        Assert.Contains(item.PermissionOptions, o => o.OptionId == "allow-once" && o.Kind == "allow_once" && o.Name == "Allow once");
        Assert.Contains(item.PermissionOptions, o => o.OptionId == "reject-once");

        await lead.AnswerPermissionRequest(caller.Session.ToString(), "allow-once", null, CancellationToken.None);
        await using var check = pg.NewContext();
        var row = await check.Sessions.AsNoTracking().SingleAsync(t => t.Id == caller.Session.Value);
        Assert.Equal("allow-once", row.PermissionOptionId);
        Assert.Equal(PermissionVerdict.Allow, row.PermissionVerdict);
    }

    // ── The audit trail ───────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Every_permission_decision_leaves_an_event_row_naming_the_answerer()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var caller = await SeedWorkingTask();
        var pending = await AskPermissionAsync(caller);
        var lead = LeadFor(new Principal.Lead(Team));

        await EscalateAsync(caller.Session, "sudo, unexplained");
        await using (var db = pg.NewContext())
        {
            await new SessionStore(db, TimeProvider.System).AnswerPermissionAsync(
                new HumanSession(), caller.Session, PermissionVerdict.Deny, "not on a shared box");
        }
        await pending.WaitAsync(Patience);

        await using var check = pg.NewContext();
        var events = await check.SessionEvents.AsNoTracking()
            .Where(e => e.SessionId == caller.Session.Value).OrderBy(e => e.Seq).ToListAsync();

        var asked = Assert.Single(events, e => e.Kind == nameof(RequestInput));
        Assert.Equal(InputRequestKind.Permission, asked.InputKind);
        Assert.Equal($"permission: {Tool} {ProposedInput}", asked.Detail);

        var escalation = Assert.Single(events, e => e.Kind == nameof(EscalatePermission));
        Assert.Equal(SessionState.BlockedOnInput, escalation.FromState);
        Assert.Equal(SessionState.BlockedOnInput, escalation.ToState);
        Assert.Contains("sudo, unexplained", escalation.Detail);

        var decision = Assert.Single(events, e => e.Kind == nameof(AnswerPermission));
        Assert.Equal(PermissionVerdict.Deny, decision.PermissionVerdict);
        // The load-bearing distinction: a person decided this, not the Lead.
        Assert.Equal(PermissionAnswerer.Human, decision.PermissionAnswerer);
        Assert.Equal("not on a shared box", decision.Detail);
        Assert.Equal(SessionState.BlockedOnInput, decision.FromState);
        Assert.Equal(SessionState.Working, decision.ToState);
    }

    [SkippableFact]
    public async Task A_lead_decision_is_recorded_as_the_leads_own()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var caller = await SeedWorkingTask();
        var pending = await AskPermissionAsync(caller, tool: "Bash", input: """{"command":"git status"}""");

        await LeadFor(new Principal.Lead(Team))
            .AnswerPermissionRequest(caller.Session.ToString(), "allow", "routine, in the workspace", CancellationToken.None);
        await pending.WaitAsync(Patience);

        await using var db = pg.NewContext();
        var decision = await db.SessionEvents.AsNoTracking()
            .Where(e => e.SessionId == caller.Session.Value && e.Kind == nameof(AnswerPermission)).SingleAsync();
        Assert.Equal(PermissionVerdict.Allow, decision.PermissionVerdict);
        Assert.Equal(PermissionAnswerer.Lead, decision.PermissionAnswerer);
    }

    // ── The §12 inbox ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task The_inbox_lists_pending_permission_requests_apart_from_questions()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var caller = await SeedWorkingTask();
        var pending = await AskPermissionAsync(caller);
        var lead = LeadFor(new Principal.Lead(Team));
        await EscalateAsync(caller.Session, "reads a credential");

        await using var db = pg.NewContext();
        var inbox = await new DashboardQueries(db, new RunnerConnectionRegistry(TimeProvider.System))
            .GetInboxAsync();

        var request = Assert.Single(inbox.PermissionRequests);
        Assert.Equal(caller.Session.Value, request.SessionId);
        Assert.Equal(Tool, request.Tool);
        Assert.Equal(ProposedInput, request.Input);
        Assert.NotNull(request.EscalatedAt);
        Assert.Equal("reads a credential", request.EscalationReason);
        Assert.Null(request.Verdict);
        // Not double-counted as an open question: it is answered by a different act.
        Assert.Empty(inbox.Questions);

        await using (var answer = pg.NewContext())
        {
            await new SessionStore(answer, TimeProvider.System).AnswerPermissionAsync(
                new HumanSession(), caller.Session, PermissionVerdict.Allow);
        }
        await pending.WaitAsync(Patience);

        await using var after = pg.NewContext();
        Assert.Empty((await new DashboardQueries(after, new RunnerConnectionRegistry(TimeProvider.System))
            .GetInboxAsync()).PermissionRequests);
    }

    /// <summary>A scope factory over the same Postgres, so the sweeper writes where these
    /// tests read.</summary>
    private IServiceScopeFactory ScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddDbContext<LandbridgeDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        services.AddLandbridgeStore();
        services.AddScoped<TokenService>();
        services.AddSingleton(TimeProvider.System);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
}
