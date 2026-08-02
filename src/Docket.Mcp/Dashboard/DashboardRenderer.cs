using System.Text;
using Docket.ControlPlane;
using Docket.Core;
using static Docket.Mcp.Dashboard.DashboardHtml;

namespace Docket.Mcp.Dashboard;

/// <summary>
/// Turns the <see cref="DashboardQueries"/> view records into the §12 pages as
/// server-rendered HTML. The JSON twin serializes the very same records, so this
/// file is only the human renderer — the two never diverge on what data exists.
/// Every dynamic value passes through <see cref="DashboardHtml.E(string)"/>; §12
/// data with no source yet renders as an explicit empty state or a labelled n/a
/// slot, never a fabricated value.
/// </summary>
internal static class DashboardRenderer
{
    // ── Machine Group view ────────────────────────────────────────────────────

    public static string Machines(IReadOnlyList<MachineView> machines, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>Machine Group</h1>");
        sb.Append("<p class=\"sub\">Connected machines, their readiness and heartbeat, and the tasks each is running.</p>");

        if (machines.Count == 0)
        {
            sb.Append(Empty("No machines connected."));
            return Page("Machine Group", "machines", sb.ToString());
        }

        foreach (var m in machines)
        {
            sb.Append("<section>");
            var readiness = m.UnderBackPressure
                ? Badge("back-pressure", "backpressure")
                : m.Ready ? Badge("ready", "ready") : Badge("not ready", "down");
            sb.Append($"<h2><code>{E(m.MachineId)}</code> {readiness} " +
                      $"<span class=\"nt\">heartbeat {E(Age(m.LastHeartbeat, now))}</span></h2>");

            sb.Append("<div class=\"pill-row\">");
            if (m.Profiles.Count == 0)
                sb.Append("<span class=\"nt\">no profiles declared</span>");
            foreach (var p in m.Profiles)
                sb.Append(Badge(p, "state-submitted"));
            sb.Append("</div>");

            if (m.RunningTasks.Count == 0)
                sb.Append(Empty("No tasks running on this machine."));
            else
            {
                sb.Append("<ul class=\"machine-tasks\">");
                foreach (var t in m.RunningTasks)
                {
                    sb.Append("<li>");
                    sb.Append($"<code>{E(t.Namespace)}</code> {StateBadge(t.State)} ");
                    sb.Append($"<span class=\"nt\">Team </span>{TeamLink(t.TeamId)}");
                    // Subagents are children in a tree (§12) but no subagent data
                    // reaches the plane yet — honest empty state, not fake columns.
                    sb.Append("<div class=\"subtree\">no subagents reported</div>");
                    sb.Append("</li>");
                }
                sb.Append("</ul>");
            }
            sb.Append("</section>");
        }

        return Page("Machine Group", "machines", sb.ToString());
    }

    // ── Team view (list) ──────────────────────────────────────────────────────

    public static string Teams(IReadOnlyList<TeamOverview> teams, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>Teams</h1>");
        sb.Append("<p class=\"sub\">Every Team by state. Idle Teams sink to the bottom. " +
                  "Parks per task is the decomposition-starvation signal (§12).</p>");

        if (teams.Count == 0)
        {
            sb.Append(Empty("No Teams yet."));
            return Page("Teams", "teams", sb.ToString());
        }

        sb.Append("<section><table><thead><tr>");
        sb.Append("<th>Team</th><th>States</th><th>Lead</th>");
        sb.Append("<th class=\"num\">Parks</th><th class=\"num\">Services</th><th class=\"num\">Open</th>");
        sb.Append("<th>Budget</th><th>Byte burn</th><th>Last activity</th>");
        sb.Append("</tr></thead><tbody>");

        foreach (var t in teams)
        {
            sb.Append($"<tr class=\"{(t.IsIdle ? "idle-row" : "")}\">");
            sb.Append($"<td>{TeamLink(t.TeamId)}<div class=\"nt\">{t.TotalTasks} tasks</div></td>");
            sb.Append($"<td>{StateCounts(t.CountsByState)}</td>");
            sb.Append($"<td>{LeadCell(t.LeadHumanId, t.LeadSince, now)}</td>");
            sb.Append($"<td class=\"num\">{t.TotalParks}</td>");
            sb.Append($"<td class=\"num\">{t.ServiceCount}</td>");
            sb.Append($"<td class=\"num\">{t.OpenInputRequests}</td>");
            // §12 lists budget + byte burn; nothing records them yet.
            sb.Append("<td class=\"nt\">n/a</td><td class=\"nt\">n/a</td>");
            sb.Append($"<td>{E(Age(t.LastActivity, now))}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></section>");
        return Page("Teams", "teams", sb.ToString());
    }

    // ── Team view (detail) — §4 reattachment surface ──────────────────────────

    public static string TeamDetail(TeamDetail team, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append($"<h1>Team <code>{E(ShortId(team.TeamId))}</code></h1>");
        sb.Append($"<p class=\"sub mono\">{E(team.TeamId)}</p>");

        // Metrics strip — the numbers a reattaching Lead scans first (§4).
        sb.Append("<section class=\"metrics\">");
        sb.Append(Metric(team.TotalTasks.ToString(), "tasks"));
        sb.Append(Metric(team.Tasks.Sum(t => t.Parks).ToString(), "parks total"));
        sb.Append(Metric(team.Services.Count.ToString(), "services"));
        sb.Append(Metric(team.OpenInputRequests.Count.ToString(), "open requests"));
        sb.Append($"<div class=\"metric\"><div class=\"n nt\">n/a</div><div class=\"l\">budget</div></div>");
        sb.Append($"<div class=\"metric\"><div class=\"n nt\">n/a</div><div class=\"l\">byte burn</div></div>");
        sb.Append("</section>");

        // Lead attached and who (§12) + last activity.
        sb.Append("<section>");
        sb.Append($"<h2>Lead</h2>{LeadCell(team.LeadHumanId, team.LeadSince, now)}");
        sb.Append($"<div class=\"nt\">last activity {E(Age(team.LastActivity, now))}</div>");
        sb.Append("</section>");

        // Tasks by state, with parks per task.
        sb.Append("<section><h2>Tasks</h2>");
        if (team.Tasks.Count == 0)
            sb.Append(Empty("No tasks."));
        else
        {
            sb.Append("<table><thead><tr>");
            sb.Append("<th>Namespace</th><th>State</th><th>Mode</th>");
            sb.Append("<th class=\"num\">Attempt</th><th class=\"num\">Parks</th><th>Detail</th><th>Report</th>");
            sb.Append("</tr></thead><tbody>");
            foreach (var t in team.Tasks)
            {
                sb.Append("<tr>");
                sb.Append($"<td><code>{E(t.Namespace)}</code></td>");
                sb.Append($"<td>{StateBadge(t.State)}</td>");
                sb.Append($"<td>{E(t.Mode.ToString())}</td>");
                sb.Append($"<td class=\"num\">{t.Attempt}</td>");
                var parks = t.Parks > 0 ? $"<span class=\"parks-hot\">{t.Parks}</span>" : "0";
                sb.Append($"<td class=\"num\">{parks}</td>");
                sb.Append($"<td>{TaskDetailCell(t, now)}</td>");
                sb.Append($"<td>{ReportCell(t)}</td>");
                sb.Append("</tr>");
            }
            sb.Append("</tbody></table>");
        }
        sb.Append("</section>");

        // Registered services (§8.2).
        sb.Append("<section><h2>Registered services</h2>");
        if (team.Services.Count == 0)
            sb.Append(Empty("No services registered."));
        else
        {
            sb.Append("<table><thead><tr><th>Name</th><th class=\"num\">Port</th><th>Task</th><th>Since</th></tr></thead><tbody>");
            foreach (var s in team.Services)
                sb.Append($"<tr><td>{E(s.Name)}</td><td class=\"num\">{s.Port}</td>" +
                          $"<td class=\"mono\">{E(ShortId(s.TaskId))}</td><td>{E(Age(s.CreatedAt, now))}</td></tr>");
            sb.Append("</tbody></table>");
            sb.Append(PreviewMintForm(team));
        }
        sb.Append("</section>");

        // Open input requests (§12). The typed kind is not persisted by the store.
        sb.Append("<section><h2>Open input requests</h2>");
        if (team.OpenInputRequests.Count == 0)
            sb.Append(Empty("Nothing blocked on input."));
        else
        {
            sb.Append("<table><thead><tr><th>Namespace</th><th>Kind</th><th>Blocked</th></tr></thead><tbody>");
            foreach (var r in team.OpenInputRequests)
                sb.Append($"<tr><td><code>{E(r.Namespace)}</code></td>" +
                          $"<td class=\"nt\">not tracked</td><td>{E(Age(r.BlockedAt, now))}</td></tr>");
            sb.Append("</tbody></table>");
        }
        sb.Append("</section>");

        return Page($"Team {ShortId(team.TeamId)}", "teams", sb.ToString());
    }

    // ── Human inbox ───────────────────────────────────────────────────────────

    public static string Inbox(InboxView inbox, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>Human inbox</h1>");
        sb.Append("<p class=\"sub\">Everything waiting on a person, across every Team.</p>");

        sb.Append("<section><h2>Open questions</h2>");
        if (inbox.Questions.Count == 0)
            sb.Append(Empty("No open questions."));
        else
        {
            sb.Append("<table><thead><tr><th>Namespace</th><th>Team</th><th>Kind</th><th>Blocked</th></tr></thead><tbody>");
            foreach (var q in inbox.Questions)
                sb.Append($"<tr><td><code>{E(q.Namespace)}</code></td><td>{TeamLink(q.TeamId)}</td>" +
                          $"<td class=\"nt\">not tracked</td><td>{E(Age(q.BlockedAt, now))}</td></tr>");
            sb.Append("</tbody></table>");
        }
        sb.Append("</section>");

        sb.Append("<section><h2>Awaiting review</h2>");
        if (inbox.AwaitingReview.Count == 0)
            sb.Append(Empty("Nothing awaiting review."));
        else
        {
            sb.Append("<table><thead><tr><th>Namespace</th><th>Team</th></tr></thead><tbody>");
            foreach (var r in inbox.AwaitingReview)
                sb.Append($"<tr><td><code>{E(r.Namespace)}</code></td><td>{TeamLink(r.TeamId)}</td></tr>");
            sb.Append("</tbody></table>");
        }
        sb.Append("</section>");

        sb.Append("<section><h2>Parked, awaiting an answer</h2>");
        if (inbox.Parked.Count == 0)
            sb.Append(Empty("No parked tasks."));
        else
        {
            sb.Append("<table><thead><tr><th>Namespace</th><th>Team</th><th>Parked on</th></tr></thead><tbody>");
            foreach (var p in inbox.Parked)
                sb.Append($"<tr><td><code>{E(p.Namespace)}</code></td><td>{TeamLink(p.TeamId)}</td>" +
                          $"<td class=\"mono\">{E(p.ParkMachine ?? "—")}</td></tr>");
            sb.Append("</tbody></table>");
        }
        sb.Append("</section>");

        // Two §12 rows that have no source yet — shown honestly, never omitted.
        sb.Append("<section><h2>Auth failures</h2>");
        sb.Append(Empty("Not recorded: the runner reports auth failures but the control plane only logs them today (§11)."));
        sb.Append("</section>");

        sb.Append("<section><h2>Permission requests</h2>");
        sb.Append(Empty("Not built yet."));
        sb.Append("</section>");

        return Page("Human inbox", "inbox", sb.ToString());
    }

    // ── Event log ─────────────────────────────────────────────────────────────

    public static string Events(IReadOnlyList<DashboardEvent> events, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>Event log</h1>");
        sb.Append("<p class=\"sub\">Recent transitions and Lead events, newest first. " +
                  "Takeovers, reboots, and evictions land here (§12).</p>");

        if (events.Count == 0)
        {
            sb.Append(Empty("No events yet."));
            return Page("Event log", "events", sb.ToString());
        }

        sb.Append("<section><table><thead><tr>");
        sb.Append("<th>When</th><th>Source</th><th>Kind</th><th>Transition</th><th>Team</th><th>Task / who</th><th>Detail</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var e in events)
        {
            sb.Append("<tr>");
            sb.Append($"<td>{E(Age(e.OccurredAt, now))}</td>");
            sb.Append($"<td>{Badge(e.Source, e.Source == "lead" ? "state-verifying" : "state-submitted")}</td>");
            sb.Append($"<td>{E(e.Kind)}</td>");
            sb.Append($"<td>{Transition(e)}</td>");
            sb.Append($"<td>{TeamLink(e.TeamId)}</td>");
            sb.Append($"<td>{EventSubject(e)}</td>");
            sb.Append($"<td>{EventDetail(e)}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></section>");
        return Page("Event log", "events", sb.ToString());
    }

    // ── Login (the first-party operator door) ─────────────────────────────────

    public static string Login(string? error, string? next = null)
    {
        var sb = new StringBuilder();
        sb.Append("<div class=\"login-wrap card\">");
        sb.Append("<h1>Sign in</h1>");
        sb.Append("<p class=\"sub\">Enter the operator passphrase to open the dashboard.</p>");
        if (!string.IsNullOrEmpty(error))
            sb.Append($"<p class=\"err\">{E(error)}</p>");
        sb.Append("<form method=\"post\" action=\"/dashboard/login\">");
        // Carry the post-login destination (e.g. the gated-preview confirm) through
        // sign-in; the POST handler restricts it to a local /dashboard path (§8.4).
        if (!string.IsNullOrEmpty(next))
            sb.Append($"<input type=\"hidden\" name=\"next\" value=\"{E(next)}\">");
        sb.Append("<input type=\"password\" name=\"passphrase\" placeholder=\"Operator passphrase\" " +
                  "autofocus autocomplete=\"current-password\">");
        // Secondary door: paste a token directly (a Lead token, or a
        // headless-minted human session). Left blank on the normal operator path.
        sb.Append("<label class=\"or\">or paste a token</label>");
        sb.Append("<input type=\"password\" name=\"token\" placeholder=\"dkt_h_… / dkt_l_…\" autocomplete=\"off\">");
        sb.Append("<button type=\"submit\">Sign in</button>");
        sb.Append("</form>");
        // This same-origin form is the first-party operator door on the
        // authorization server itself; third-party MCP clients (e.g. Claude Code)
        // sign in through the OAuth 2.1 flow (§5) instead.
        sb.Append("<p class=\"seam\">Third-party clients (e.g. Claude Code) sign in through the OAuth 2.1 " +
                  "flow. This form is the first-party operator door on the authorization server.</p>");
        sb.Append("</div>");
        return Page("Sign in", "", sb.ToString(), autoRefresh: false);
    }

    /// <summary>
    /// The 'Create preview' control on the Team view's registered-services section
    /// (§12 mint, §8.4). Posts to <c>/dashboard/preview</c>; the service option value
    /// is <c>{taskId}:{name}</c> so the mapping binds the exact owning task.
    /// </summary>
    private static string PreviewMintForm(TeamDetail team)
    {
        var sb = new StringBuilder();
        sb.Append("<form class=\"preview-mint\" method=\"post\" action=\"/dashboard/preview\">");
        sb.Append($"<input type=\"hidden\" name=\"teamId\" value=\"{E(team.TeamId.ToString())}\">");
        sb.Append("<strong>Create preview</strong> ");
        sb.Append("<select name=\"service\" aria-label=\"service\">");
        foreach (var s in team.Services)
            sb.Append($"<option value=\"{E(s.TaskId + ":" + s.Name)}\">{E(s.Name)}</option>");
        sb.Append("</select> ");
        sb.Append("<select name=\"auth\" aria-label=\"visibility\">");
        sb.Append("<option value=\"gated\">gated (operator only)</option>");
        sb.Append("<option value=\"public\">public (anyone with the link)</option>");
        sb.Append("</select> ");
        sb.Append("<input type=\"number\" name=\"ttl\" min=\"1\" placeholder=\"TTL min\" aria-label=\"ttl minutes\"> ");
        sb.Append("<button type=\"submit\">Create</button>");
        sb.Append("</form>");
        return sb.ToString();
    }

    /// <summary>
    /// The worker's in-band report (§10), rendered verbatim-escaped behind a
    /// disclosure so the task table stays compact. It is agent-authored text (§13):
    /// escaped through <see cref="E"/> and never interpreted, only shown.
    /// </summary>
    private static string ReportCell(TeamTaskView t) =>
        t.Report is { Length: > 0 } r
            ? $"<details><summary>report</summary><pre class=\"report\">{E(r)}</pre></details>"
            : "<span class=\"nt\">—</span>";

    /// <summary>§9 check 4 completion provenance, humanized for the task view.</summary>
    private static string ProvenanceLabel(Docket.Core.VerdictProvenance p) => p switch
    {
        Docket.Core.VerdictProvenance.LeadSession => "lead session",
        Docket.Core.VerdictProvenance.Human => "a human",
        _ => p.ToString(),
    };

    /// <summary>The result page after a dashboard mint (§12): the shareable URL to copy.</summary>
    public static string PreviewCreated(string url, Docket.Core.PreviewAuthPolicy policy, DateTimeOffset expiresAt, Guid teamId)
    {
        var sb = new StringBuilder();
        sb.Append("<section class=\"card\"><h1>Preview created</h1>");
        sb.Append($"<p class=\"sub\">{E(policy.ToString().ToLowerInvariant())} preview — expires {E(expiresAt.ToString("u"))}.</p>");
        sb.Append($"<p><a class=\"preview-url mono\" href=\"{E(url)}\">{E(url)}</a></p>");
        if (policy == Docket.Core.PreviewAuthPolicy.Public)
            sb.Append("<p class=\"nt\">Anyone with this link can open it until it expires. Public previews are short-lived by design.</p>");
        else
            sb.Append("<p class=\"nt\">Opening this link requires a Docket operator session in the browser.</p>");
        sb.Append($"<p><a href=\"/dashboard/teams/{teamId}\">← back to the Team</a></p>");
        sb.Append("</section>");
        return Page("Preview created", "teams", sb.ToString(), autoRefresh: false);
    }

    /// <summary>The gated-browser-flow confirm error page (§8.4): a bad label, expired preview, or wrong Team.</summary>
    public static string PreviewAuthError(string message)
    {
        var sb = new StringBuilder();
        sb.Append("<div class=\"login-wrap card\">");
        sb.Append("<h1>Preview unavailable</h1>");
        sb.Append($"<p class=\"err\">{E(message)}</p>");
        sb.Append("<p><a href=\"/dashboard/machines\">← dashboard</a></p>");
        sb.Append("</div>");
        return Page("Preview unavailable", "", sb.ToString(), autoRefresh: false);
    }

    // ── Fragments ─────────────────────────────────────────────────────────────

    private static string TeamLink(Guid teamId) =>
        teamId == Guid.Empty
            ? "<span class=\"nt\">—</span>"
            : $"<a class=\"mono\" href=\"/dashboard/teams/{teamId}\">{E(ShortId(teamId))}</a>";

    private static string Metric(string n, string label) =>
        $"<div class=\"metric\"><div class=\"n\">{E(n)}</div><div class=\"l\">{E(label)}</div></div>";

    private static string StateCounts(IReadOnlyDictionary<TaskState, int> counts)
    {
        if (counts.Count == 0)
            return "<span class=\"nt\">—</span>";
        var sb = new StringBuilder("<div class=\"pill-row\">");
        foreach (var state in Enum.GetValues<TaskState>())
            if (counts.TryGetValue(state, out var n) && n > 0)
                sb.Append($"<span class=\"badge state-{state.ToString().ToLowerInvariant()}\">{E(state.ToString())} {n}</span>");
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string LeadCell(Guid? humanId, DateTimeOffset? since, DateTimeOffset now) =>
        humanId is { } h
            ? $"{Badge("attached", "ready")} <span class=\"mono\">{E(ShortId(h))}</span> " +
              $"<span class=\"nt\">since {E(Age(since, now))}</span>"
            : "<span class=\"nt\">leadless</span>";

    private static string TaskDetailCell(TeamTaskView t, DateTimeOffset now)
    {
        var parts = new List<string>();
        // §6/§11 Y-continues-X lineage: a continuation task resumed a prior task's
        // session. Shown for any state (a continuation may be submitted/working, not
        // just blocked/parked), alongside the state-specific detail below.
        if (t.ContinuesTaskId is { } prior)
            parts.Add($"<span class=\"nt\">continues <span class=\"mono\">{E(ShortId(prior))}</span></span>");
        if (t.State == TaskState.BlockedOnInput)
            parts.Add($"<span class=\"nt\">blocked {E(Age(t.BlockedAt, now))}</span>");
        else if (t.State == TaskState.Parked && t.ParkMachine is not null)
            parts.Add($"<span class=\"nt\">parked on <span class=\"mono\">{E(t.ParkMachine)}</span></span>");
        // §9 check 4: who adjudicated a completed task — lead-session or human.
        else if (t.State == TaskState.Completed && t.CompletionProvenance is { } who)
            parts.Add($"<span class=\"nt\">accepted by {E(ProvenanceLabel(who))}</span>");
        return string.Join(" ", parts);
    }

    private static string Transition(DashboardEvent e) =>
        e is { FromState: { } f, ToState: { } to }
            ? $"{StateBadge(f)} → {StateBadge(to)}"
            : e.ToState is { } only ? $"→ {StateBadge(only)}" : "<span class=\"nt\">—</span>";

    private static string EventSubject(DashboardEvent e)
    {
        if (e.Source == "lead")
        {
            var who = e.HumanId is { } h ? ShortId(h) : "—";
            var prior = e.PriorHumanId is { } p ? $" (was {ShortId(p)})" : "";
            return $"<span class=\"mono\">{E(who)}</span><span class=\"nt\">{E(prior)}</span>";
        }
        return e.Namespace is { } ns ? $"<code>{E(ns)}</code>" : "<span class=\"nt\">—</span>";
    }

    /// <summary>
    /// The event's structured detail (§10/§12, #50). The derived-telemetry kinds
    /// render their own facts — the typed input-request kind, the auth-failure
    /// operation/target/code/scope, the subagent lineage — and a plain transition
    /// falls back to its effect-name detail. All values pass through <see cref="E"/>.
    /// </summary>
    private static string EventDetail(DashboardEvent e)
    {
        if (e.InputKind is { } kind)
            return Badge(kind.ToString(), "state-blockedoninput");

        if (e.Kind == TaskEventRow.AuthFailedKind)
        {
            var scope = e.AuthMissingScope is { } s ? $", missing scope <code>{E(s)}</code>" : "";
            return $"<span class=\"nt\">{E(e.AuthOperation ?? "—")} on <code>{E(e.AuthTarget ?? "—")}</code> " +
                   $"failed <code>{E(e.AuthErrorCode ?? "—")}</code>{scope}</span>";
        }

        if (e.Kind == TaskEventRow.SubagentSpawnedKind)
        {
            // Lineage is progressive enhancement (§10): a harness may report neither id.
            var agent = e.SubagentId is { } a ? $"<code>{E(a)}</code>" : "<span class=\"nt\">unnamed</span>";
            var parent = e.SubagentParentId is { } p ? $" under <code>{E(p)}</code>" : "";
            return $"<span class=\"nt\">subagent </span>{agent}{parent}";
        }

        return string.IsNullOrEmpty(e.Detail) ? "<span class=\"nt\">—</span>" : $"<span class=\"nt\">{E(e.Detail)}</span>";
    }
}
