using System.ComponentModel;
using Landbridge.Contracts;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Landbridge.Mcp.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using static Landbridge.Mcp.Tools.ToolResults;

namespace Landbridge.Mcp.Tools;

/// <summary>
/// The Lead tool surface (spec §10). A Lead is a harness client a human drives
/// (§4); these tools map onto the engine commands a lead claim authorizes.
///
/// The caller is never a parameter — it comes from the authenticated token
/// (HttpContext.User → Lead factory), exactly like <see cref="WorkerTools"/>.
/// The factory owns Teams via <c>create_team</c>; other tools take a
/// <c>teamId</c> this credential owns. Each tool is a thin adapter over an
/// already-tested <see cref="SessionStore"/> transition; the store and the engine
/// re-check authority (§9 check 3 for creation, the §7 human-confirmation gate
/// for review, disposition for cancel), so nothing here interprets session content.
///
/// <para><c>list_profiles</c> is the one tool with no Team in it and no store behind it: a
/// declared runner profile is machine config no Team owns, and it is read straight off the
/// live <see cref="RunnerConnectionRegistry"/> (§7 routing, §10 as-built refinement). That
/// makes the lead-claim check at its top the whole of its authority rather than a
/// pre-filter, which is why it is written as a deliberate read of
/// <see cref="LeadPrincipal"/> rather than left to a downstream re-check that does not
/// exist for it.</para>
///
/// <para>The last three tools are the §8.3 <b>human path</b>: a Lead binds an
/// enrolled machine as its human's own, then opens a forward whose consumer end is
/// that machine — how a person reaches a raw-TCP service (a worker's Postgres) from
/// a local client. Same grants, same relay, same splice as the worker's
/// <c>open_forward</c>; only the consumer end differs.</para>
/// </summary>
[McpServerToolType]
public sealed class LeadTools(
    SessionStore store,
    RunnerConnectionRegistry registry,
    LeadMachineBindingService bindings,
    RelayGrantService grants,
    ForwardOrchestrator forwards,
    TokenService tokens,
    IHttpContextAccessor http,
    IConfiguration config,
    SessionEventFanout? inbox = null)
{
    /// <summary>
    /// The live lead principal behind this call — Team and the claiming human (§4).
    /// An evicted claim is refused with an explicit reason — evicted by whom, when —
    /// rather than a bare authorization error, so the displaced session's harness
    /// does not invent an explanation for the denial.
    /// </summary>
    private Principal.Lead LeadPrincipal
    {
        get
        {
            var user = http.HttpContext?.User ?? throw Unauthorized();
            if (LandbridgeClaims.AsEvictedLead(user) is { } evicted)
                throw new McpException(
                    $"your lead claim on team {evicted.Team.Value:N} was taken over by human " +
                    $"{evicted.EvictedByHuman:N} at {evicted.EvictedAt:O}; reattach to the Team to continue.");
            return LandbridgeClaims.AsLeadPrincipal(user) ?? throw Unauthorized();
        }
    }

    /// <summary>
    /// The claiming human, for the facts that key on the person rather than the Team
    /// — currently only the lead↔machine binding (§8.3 human path). A lead credential
    /// with no human attribution can authenticate but owns no machine.
    /// </summary>
    private static Guid HumanOf(Principal.Lead lead) =>
        lead.HumanId ?? throw new McpException(
            "this lead credential carries no human identity, so it cannot own a machine binding; " +
            "re-claim the Team from your human session (/landbridge-lead) and try again.");

    /// <summary>
    /// Resolve the engine actor for <paramref name="teamId"/> after an ownership
    /// check. The factory token is not itself a Team; the id is the capability.
    /// </summary>
    private async Task<LeadClaim> LeadOn(string teamId, CancellationToken ct)
    {
        var lead = LeadPrincipal;
        if (string.IsNullOrWhiteSpace(teamId) || !Guid.TryParse(teamId, out var g))
            throw new McpException(
                "teamId is required and must be a team id you minted with create_team, or one a human gave you.");
        var team = new TeamId(g);
        if (!await tokens.OwnsTeamAsync(lead.CredentialId, team, ct))
            throw new McpException(
                "this lead credential does not own that team; create_team or use a team id you were given.");
        return new LeadClaim(team);
    }

    [McpServerTool(Name = "create_team"),
     Description("Mint a new Team owned by this Lead token and return its id. Use that id as teamId on " +
                 "every other Lead tool. Call this when a human did not give you a team id. Do not write " +
                 "the id into the project — it is the capability that keeps parallel agents on this " +
                 "token from sharing a Team. There is no list of Teams.")]
    public async Task<string> CreateTeam(CancellationToken ct = default)
    {
        var team = await tokens.CreateTeamAsync(LeadPrincipal.CredentialId, ct);
        return team.Value.ToString();
    }

    [McpServerTool(Name = "create_session"),
     Description("Create a session for this Team. Only a Lead may create sessions. The description is the " +
                 "whole brief (what to do and how you will judge it); the plane never parses it. Profile is " +
                 "required — call list_profiles first and pass an exact name. The worker isolates itself. " +
                 "More work on an existing worker is send_input_request on that session id (it unhides a " +
                 "stopped row). Returns the new session id.")]
    public async Task<string> CreateSession(
        [Description("Opaque, non-empty prose: what to accomplish and how you will judge it. " +
                     "Read by the worker, never parsed by the control plane.")]
        string description,
        [Description("Runner profile name for exact-match routing. Required. Call list_profiles first — " +
                     "a name no machine declares makes a session nothing can ever claim.")]
        string profile,
        [Description("The Team this session belongs to. From create_team, or a human-supplied id.")]
        string teamId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(description))
            throw new McpException("description must be non-empty; it is the worker's instructions.");
        if (string.IsNullOrWhiteSpace(profile))
            throw new McpException("profile is required; call list_profiles and pass an exact name.");

        var lead = await LeadOn(teamId, ct);
        var result = await store.CreateAsync(
            new CreateSession(lead, lead.Team, description, profile.Trim()), ct);

        return result switch
        {
            StoreResult.Applied a => a.Session.Id.ToString(),
            _ => throw Rejection(result),
        };
    }

    /// <summary>Default process wind-down before the runner hard-kills.</summary>
    public static readonly TimeSpan DefaultStopTtl = TimeSpan.FromMinutes(5);

    [McpServerTool(Name = "stop_session"),
     Description("Hide this session and release occupancy. The worker gets a wind-down " +
                 "(default 5 minutes) then a kill. Not a grade of the work — more work on the " +
                 "same worker is send_input_request (it unhides), not this. Allowed mid-exchange (a question " +
                 "or a live permission wait). A session's own worker can never stop it. park_session " +
                 "releases occupancy without hiding.")]
    public async Task<string> StopSession(
        [Description("The session to stop.")] string sessionId,
        [Description("The Team that owns this session. From create_team, or a human-supplied id.")]
        string teamId,
        CancellationToken ct,
        [Description("Seconds to wait for a graceful stop before kill. Default 300 (5 minutes). " +
                     "0 kills immediately.")]
        int? ttlSeconds = null)
    {
        var ttl = ResolveStopTtl(ttlSeconds);
        var lead = await LeadOn(teamId, ct);
        var id = ParseSessionId(sessionId);
        var machine = registry.MachineFor(id);
        var applied = await store.ApplyAsync(id, new Landbridge.Core.StopSession(lead), ct);
        if (applied is StoreResult.Applied ok
            && ok.Session.OccupancyObserved == Occupancy.Running
            && machine is { Length: > 0 })
        {
            await registry.SendAsync(
                machine,
                new StopCommand(id, ttl, StopDisposition.Preserve, "stop"),
                ct);
        }
        return Describe(applied);
    }

    private static TimeSpan ResolveStopTtl(int? ttlSeconds)
    {
        if (ttlSeconds is null)
            return DefaultStopTtl;
        if (ttlSeconds < 0)
            throw new McpException("ttlSeconds must be >= 0 (0 kills immediately).");
        return TimeSpan.FromSeconds(ttlSeconds.Value);
    }

    [McpServerTool(Name = "park_session"),
     Description("Release occupancy on purpose (desired=on_disk) without hiding the row. The worker " +
                 "is cancelled; it is not a timer. Refused while a permission wait is live. Use this to " +
                 "free the machine when you are done waiting. Answering a still-live wait is " +
                 "send_input_response, not this. Wake later is send_input_request (session/load).")]
    public async Task<string> ParkTask(
        [Description("The session to park.")] string sessionId,
        [Description("The Team that owns this session. From create_team, or a human-supplied id.")]
        string teamId,
        CancellationToken ct)
    {
        var lead = await LeadOn(teamId, ct);
        var id = ParseSessionId(sessionId);
        var machine = registry.MachineFor(id) ?? "unknown";
        var result = await store.ApplyAsync(id, new Park(lead, new ParkRecord(machine)), ct);
        if (result is StoreResult.Applied && machine != "unknown")
        {
            await registry.SendAsync(
                machine,
                new StopCommand(id, TimeSpan.FromSeconds(30), StopDisposition.PreserveAndPark, "park"),
                ct);
        }
        return Describe(result);
    }

    [McpServerTool(Name = "send_input_response"),
     Description("Close a wait the worker opened: a question, spawn_request, auth_help, or endpoint_wait. " +
                 "Read first with get_lead_inbox(sessionId). Pass your words as 'answer' — that text is " +
                 "the only thing the worker receives. Refused if nothing is waiting (use send_input_request) " +
                 "or if a permission wait is live (use answer_permission_request). A still-live ACP session " +
                 "gets a follow-up prompt on the same instance; a dead or parked waiter is redispatched " +
                 "with its transcript resumed.")]
    public async Task<string> SendInputResponse(
        [Description("The session id that is waiting on you.")]
        string sessionId,
        [Description("The Team that owns this session. From create_team, or a human-supplied id.")]
        string teamId,
        [Description("Your answer, in prose: the decision, and enough of why for the worker to apply it to cases " +
                     "you did not enumerate. It reaches the worker on its next get_inbox. Capped at 16 KB; " +
                     "over-cap is refused and the session stays blocked, so point at a reference for detail. " +
                     "Omit only to unblock a session that needs no words (an endpoint_wait whose service is up).")]
        string? answer = null,
        CancellationToken ct = default)
    {
        var lead = await LeadOn(teamId, ct);
        var id = ParseSessionId(sessionId);
        var machine = registry.MachineFor(id);
        var live = registry.HasLiveProcess(id);
        var result = await store.SendInputResponseAsync(lead, id, machine, answer, live, ct);
        return await DoorbellIfLive(id, machine, live, result, ct);
    }

    [McpServerTool(Name = "send_input_request"),
     Description("Talk to a worker that is not waiting on you: a live follow-up, a parked wake, a " +
                 "stopped session (unhides and session/load), or a failed retry (session/new). Pass " +
                 "your words as 'text' — that is the only thing the worker receives. Refused while a " +
                 "question wait is open (use send_input_response) or a permission wait is live (use " +
                 "answer_permission_request). A still-live ACP session stays on the same instance; a " +
                 "dead, parked, stopped, or failed session is redispatched.")]
    public async Task<string> SendInputRequest(
        [Description("The session id to talk to.")]
        string sessionId,
        [Description("The Team that owns this session. From create_team, or a human-supplied id.")]
        string teamId,
        [Description("What the worker should do next. It reaches the worker on its next get_inbox. " +
                     "Capped at 16 KB; over-cap is refused. Omit only to wake a parked, stopped, or " +
                     "failed session that needs no words.")]
        string? text = null,
        CancellationToken ct = default)
    {
        var lead = await LeadOn(teamId, ct);
        var id = ParseSessionId(sessionId);
        var machine = registry.MachineFor(id);
        var live = registry.HasLiveProcess(id);
        var result = await store.SendInputRequestAsync(lead, id, machine, text, live, ct);
        return await DoorbellIfLive(id, machine, live, result, ct);
    }

    private async Task<string> DoorbellIfLive(
        SessionId id, string? machine, bool live, StoreResult result, CancellationToken ct)
    {
        if (result is StoreResult.Applied applied
            && applied.Session.State == SessionState.Working
            && applied.Session.CurrentInstance is not null
            && live
            && machine is { } dest)
        {
            await registry.SendAsync(dest, new PromptCommand(id), ct);
        }
        return Describe(result);
    }

    [McpServerTool(Name = "answer_permission_request"),
     Description("Decide a permission request from a worker's harness (§11) by picking ONE of the " +
                 "options get_lead_inbox(sessionId) listed (the harness's own optionId). Unlike every other " +
                 "blocked session, THE WORKER IS STILL RUNNING and blocked inside this call — your " +
                 "choice resumes it in place, so answer promptly. 'allow'/'deny' still work as aliases " +
                 "for the matching kind when you have not picked a specific optionId. Approve routine " +
                 "workspace operations that follow from the session you wrote. When you cannot justify " +
                 "a call from that description — credentials or keychain access, network egress beyond " +
                 "the hosts the session needs, destructive operations outside the workspace, sudo — " +
                 "deny with a message saying why and what to do instead. A reject-kind option " +
                 "MUST carry a message: it is guidance the worker reads and adapts to.")]
    public async Task<string> AnswerPermissionRequest(
        [Description("The session id whose permission request is pending.")]
        string sessionId,
        [Description("The Team that owns this session. From create_team, or a human-supplied id.")]
        string teamId,
        [Description("One optionId from get_lead_inbox(sessionId), or 'allow'/'deny' as aliases for the " +
                     "matching harness kind.")]
        string option,
        [Description("What the worker is told. Required on a reject-kind option — a refusal it cannot " +
                     "read is one it will retry, so say why and what to do instead. Optional on an allow. " +
                     "Capped at 16 KB.")]
        string? message = null,
        CancellationToken ct = default)
    {
        var lead = await LeadOn(teamId, ct);
        var id = ParseSessionId(sessionId);
        if (string.IsNullOrWhiteSpace(option))
            throw new McpException(
                "option is required: pick an optionId from get_lead_inbox(sessionId), or 'allow'/'deny'.");

        // No park record and no lease machine: this path resumes the worker that is still
        // holding its own tool call open, so there is nothing to redispatch and no transcript
        // to resume (§11). Escalation is enforced on the row inside the store, so a Lead
        // answering one it already handed over is refused there rather than here.
        return Describe(await store.AnswerPermissionAsync(lead, id, option.Trim(), message, ct));
    }

    [McpServerTool(Name = "get_lead_inbox"),
     Description("Read this Team's outstanding inbox items right now: failed, permission, report, " +
                 "question / spawn_request / auth_help, and pull (worker-owed). Team-wide is identifiers " +
                 "only. Pass sessionId or sessionIds to fetch one or more sessions WITH BODIES " +
                 "(result reference, report, question, permission options, infrastructure account). " +
                 "That per-session fetch is the pull: unread report mail is marked read. A question or " +
                 "permission wait stays outstanding until you answer it. Hidden rows are omitted. A " +
                 "failed session still lists a leftover envelope as a second item. For a wake when the " +
                 "inbox is empty, watch_lead_inbox.")]
    public async Task<LeadInboxView> GetLeadInbox(
        [Description("The Team whose inbox to read. From create_team, or a human-supplied id.")]
        string teamId,
        [Description("Optional: only this session's outstanding items, with bodies.")] string? sessionId = null,
        CancellationToken ct = default,
        [Description("Optional: these sessions' outstanding items, with bodies.")] string[]? sessionIds = null)
    {
        var lead = await LeadOn(teamId, ct);
        var filter = SessionFilter(sessionId, sessionIds);
        return await store.GetLeadInboxAsync(lead.Team, filter, ct, filter is { Count: > 0 } ? lead : null);
    }

    [McpServerTool(Name = "watch_lead_inbox"),
     Description("The Lead inbox feed. Returns all outstanding items as soon as any exist " +
                 "(failed, permission, report, question / spawn_request / auth_help, pull). If the " +
                 "inbox is empty it waits until something is outstanding, then returns that snapshot. " +
                 "Team-wide is identifiers only. Pass sessionId or sessionIds to watch those sessions " +
                 "WITH BODIES; unread report mail is marked read when it arrives. A question or " +
                 "permission wait stays until you answer. Call again after you act. HTTP twin: " +
                 "GET /lead/inbox/events.")]
    public async Task<LeadInboxView> WatchLeadInbox(
        [Description("The Team whose inbox to watch. From create_team, or a human-supplied id.")]
        string teamId,
        [Description("Optional: only this session's outstanding items, with bodies.")] string? sessionId = null,
        CancellationToken ct = default,
        [Description("Optional: these sessions' outstanding items, with bodies.")] string[]? sessionIds = null)
    {
        if (inbox is null)
            throw new McpException("the inbox feed is not available in this process.");
        var lead = await LeadOn(teamId, ct);
        var filter = SessionFilter(sessionId, sessionIds);
        var actor = filter is { Count: > 0 } ? lead : (Actor?)null;
        await foreach (var snapshot in LeadInboxWatch.Snapshots(store, inbox, lead.Team, filter, actor, ct))
        {
            if (snapshot.Items.Count > 0)
                return snapshot;
        }
        return new LeadInboxView([]);
    }

    [McpServerTool(Name = "get_team_state"),
     Description("Read this Team's occupancy (desired/observed), health, hidden, and message state, " +
                 "plus a per-session structural summary. Counts and flags only, never prose — each " +
                 "session shows has_report and has_question plus input_kind (the typed kind of request " +
                 "it is waiting on). For outstanding items that need you, including the words, " +
                 "prefer get_lead_inbox / watch_lead_inbox (pass sessionIds to pull bodies). Also reports which machine you have bound " +
                 "as your human's own (bound_machine, null if none) — the consumer end " +
                 "open_lead_forward needs.")]
    public async Task<TeamStateView> GetTeamState(
        [Description("The Team to read. From create_team, or a human-supplied id.")]
        string teamId,
        CancellationToken ct)
    {
        var actor = await LeadOn(teamId, ct);
        var lead = LeadPrincipal;
        var state = await store.GetTeamStateAsync(actor.Team, ct);
        // The binding keys on the human, not the Team, so it is composed on here
        // rather than read out of the Team's rows (§8.3 human path). A lead
        // credential with no human attribution simply shows no binding.
        if (lead.HumanId is not { } human)
            return state;
        return await bindings.GetAsync(human, ct) is { } bound
            ? state with { BoundMachine = new LeadMachineView(bound.MachineId, bound.MachineName, bound.BoundAt) }
            : state;
    }

    [McpServerTool(Name = "list_profiles"),
     Description("List the runner profiles the fleet currently declares, and for each one the " +
                 "machines offering it and whether they can take work right now. Read this BEFORE " +
                 "passing a profile to create_session: routing is exact-match (§7), so a session naming a " +
                 "profile no machine declares sits unclaimable indefinitely and nothing reports why. " +
                 "A name absent from this list is a name no session should carry. 'dispatchable' false " +
                 "with machines listed means the profile exists but every machine offering it is " +
                 "saturated or not yet ready — that session will queue and then run, so wait rather than " +
                 "re-route. Profile is required on create_session. Read-only, and NOT the machine " +
                 "group: it carries no sessions, Teams, services or processes — that view is human-only " +
                 "(§12), and your operator reads it on /dashboard/machines. Each machine lists its " +
                 "enrolled name and OS: the same machineId on two profiles is one box.")]
    public async Task<ProfileRoutingView> ListProfiles(CancellationToken ct)
    {
        _ = LeadPrincipal;

        var view = registry.ProfileRouting();
        var ids = view.Profiles.SelectMany(p => p.Machines).Select(m => m.MachineId).Distinct();
        var labels = await store.GetMachineLabelsAsync(ids, ct);
        if (labels.Count == 0)
            return view;

        var profiles = view.Profiles.Select(p => p with
        {
            Machines = p.Machines.Select(m =>
                labels.TryGetValue(m.MachineId, out var label)
                    ? m with { Name = label.Name, Os = label.Os }
                    : m).ToList(),
        }).ToList();
        return view with { Profiles = profiles };
    }

    // ── The human path to a service (§8.3) ────────────────────────────────────

    [McpServerTool(Name = "bind_machine"),
     Description("Claim an enrolled machine as your human's OWN machine — the box they are sitting at " +
                 "(spec §8.3). This is what makes open_lead_forward possible: it needs somewhere to bind " +
                 "a local port, and a Lead has no machine of its own. The machine must already be enrolled " +
                 "with landbridged installed. On that box GET http://127.0.0.1:19378 — landbridged answers " +
                 "with the machine id; pass it here. Enrollment stdout and the dashboard Machine Group view " +
                 "also have it. One machine per person, and one person per machine: if you have moved, " +
                 "unbind_machine first. Only bind a machine your human actually controls — a forward will " +
                 "open a listening port on it.")]
    public async Task<string> BindMachine(
        [Description("The enrolled machine's id (a uuid). On the box you are sitting at, GET http://127.0.0.1:19378 — landbridged answers with it.")]
        string machineId,
        [Description("A Team this Lead owns. From create_team, or a human-supplied id.")]
        string teamId,
        CancellationToken ct)
    {
        _ = await LeadOn(teamId, ct);
        var human = HumanOf(LeadPrincipal);
        if (!Guid.TryParse(machineId, out var id))
            throw new McpException($"'{machineId}' is not a valid machine id (expected a uuid).");

        return await bindings.BindAsync(human, id, ct) switch
        {
            LeadMachineBindResult.Bound b =>
                $"ok: machine {b.Binding.MachineName} ({b.Binding.MachineId:D}) is now your machine; " +
                "open_lead_forward will bind its loopback ports.",
            LeadMachineBindResult.Refused r => throw new McpException($"bind_machine refused: {r.Reason}"),
            _ => throw new McpException("unknown bind result"),
        };
    }

    [McpServerTool(Name = "unbind_machine"),
     Description("Release your human's machine binding (spec §8.3). Do this when they move to a different " +
                 "machine, or when the machine should no longer be a forward target. Already-established " +
                 "forwards are not severed — a splice lives until its owning session leaves working — but no " +
                 "new open_lead_forward will resolve until a machine is bound again.")]
    public async Task<string> UnbindMachine(
        [Description("A Team this Lead owns. From create_team, or a human-supplied id.")]
        string teamId,
        CancellationToken ct)
    {
        _ = await LeadOn(teamId, ct);
        var released = await bindings.UnbindAsync(HumanOf(LeadPrincipal), ct);
        return released is null
            ? "ok: you had no machine bound; nothing to release."
            : $"ok: released machine {released.MachineName} ({released.MachineId:D}); " +
              "open_lead_forward will refuse until you bind one again.";
    }

    [McpServerTool(Name = "open_lead_forward"),
     Description("Open a forward from YOUR human's bound machine to a service registered by a session in " +
                 "this Team (spec §8.3) — the way a person reaches a non-HTTP service, e.g. connecting " +
                 "psql to a worker's Postgres. Returns a loopback host and port on the bound machine that " +
                 "a local client connects to directly; hand it to your human as a command to run. Only " +
                 "services registered by a currently-working session in your Team are forwardable. TWO limits " +
                 "to pass on: the address carries exactly ONE connection, and it must be used within about " +
                 "two minutes or the listener closes — so open it when your human is ready to connect, and " +
                 "call again for another session. For a browser-reachable HTTP service, the worker's " +
                 "open_preview URL is the better path (no bound machine needed).")]
    public async Task<OpenForwardResult> OpenLeadForward(
        [Description("The name of a service registered by a working session in your Team.")]
        string serviceName,
        [Description("The Team that owns the service. From create_team, or a human-supplied id.")]
        string teamId,
        CancellationToken ct)
    {
        var actor = await LeadOn(teamId, ct);
        var lead = LeadPrincipal;
        var human = HumanOf(lead);

        // 1. Where does this person sit? Nothing infers it — the binding is the only
        // answer, and its absence is a first-class, actionable refusal (§8.3).
        var bound = await bindings.GetAsync(human, ct)
            ?? throw new McpException(
                "you have no machine bound, so there is nowhere to open a local port. Three steps: " +
                "install and enroll landbridged on the machine your human is sitting at, " +
                "GET http://127.0.0.1:19378 for that box's machine id, bind_machine with it, then call " +
                "open_lead_forward again. " +
                "If the service speaks HTTP, its worker can mint a browser preview URL with open_preview " +
                "instead — that needs no landbridged on your human's side.");

        // 2. Issue the grant. Same check-11 gate as a worker's open_forward, scoped
        // to this Lead's own Team (§9 check 11, §8.2).
        var issued = await grants.IssueForLeadAsync(actor.Team, serviceName, ct) switch
        {
            RelayGrantResult.Issued i => i,
            RelayGrantResult.Refused r => throw new McpException($"rejected ({r.Rule}): {r.Reason}"),
            _ => throw new McpException("unknown grant result"),
        };

        // 3. Same orchestration as the worker path: the bound machine's landbridged is
        // the consumer end and reports the loopback port it bound; the grant and
        // relay URL stay inside landbridged and never reach this agent (§8.3).
        return await forwards.EstablishForLeadAsync(
                bound.MachineId.ToString(), issued, serviceName, WorkerTools.RelayUrlFrom(config), ct) switch
        {
            ForwardEstablishResult.Established e => new OpenForwardResult(
                WorkerTools.ForwardLoopbackHost, e.Port, issued.ForwardId.ToString(), issued.ExpiresAt),
            ForwardEstablishResult.Failed f => throw new McpException($"open_lead_forward failed: {f.Reason}"),
            _ => throw new McpException("unknown forward result"),
        };
    }

    private static SessionId ParseSessionId(string sessionId) =>
        Guid.TryParse(sessionId, out var g)
            ? new SessionId(g)
            : throw new McpException($"'{sessionId}' is not a valid session id.");

    private static IReadOnlyList<Guid>? SessionFilter(string? sessionId, string[]? sessionIds)
    {
        var ids = new List<Guid>();
        if (!string.IsNullOrWhiteSpace(sessionId))
            ids.Add(ParseSessionId(sessionId).Value);
        if (sessionIds is { Length: > 0 })
        {
            foreach (var raw in sessionIds)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                ids.Add(ParseSessionId(raw).Value);
            }
        }
        return ids.Count == 0 ? null : ids;
    }

    private static McpException Unauthorized() =>
        new("this tool requires a live lead claim; claim the Team first.");
}
