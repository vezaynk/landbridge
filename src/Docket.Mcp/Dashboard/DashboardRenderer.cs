using System.Globalization;
using System.Text;
using Docket.Contracts;
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
        sb.Append("<p class=\"sub\">Connected machines, their readiness and heartbeat, the tasks each is " +
                  "running, and whether a human has bound it as their own machine (§8.3).</p>");

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

            // §8.3 human path: a bound machine is somebody's own box and a
            // Lead-facing forward target. Shown for the same reason readiness is.
            if (m.BoundToHuman is { } boundTo)
                sb.Append($"<p class=\"nt\">bound {E(Age(m.BoundAt, now))} as the own machine of human " +
                          $"<span class=\"mono\">{E(ShortId(boundTo))}</span> — a Lead-facing forward target</p>");

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

            AppendServices(sb, m, now);
            AppendProcesses(sb, m, now);
            sb.Append("</section>");
        }

        // §10/§12: agent-started processes, in their own table. A process is a job — never
        // restarted, `exited` is where it rests — and nothing reclaims it when its declaring
        // task ends, so this view is also the safety net that makes an un-cleaned-up process
        // visible to the operator.
        static void AppendProcesses(StringBuilder sb, MachineView m, DateTimeOffset now)
        {
            if (m.Processes is not { Count: > 0 })
                return;

            sb.Append("<div class=\"machine-services\">");
            sb.Append("<h3>Background processes <span class=\"nt\">started by agents on this machine</span></h3>");
            sb.Append("<table><thead><tr>");
            // No port column: Docket tracks no port for a process (§10). Stdin is here because a
            // cleanup agent needs to know whether a graceful stop exists.
            sb.Append("<th>Process</th><th>State</th><th>Stdin</th><th>Up for</th>" +
                      "<th>Exit</th><th>Ended</th><th>Started by task</th>");
            sb.Append("</tr></thead><tbody>");

            foreach (var p in m.Processes)
            {
                var badge = p.State switch
                {
                    ServiceState.Running => Badge("running", "ready"),
                    ServiceState.Starting => Badge("starting", "state-submitted"),
                    ServiceState.Exited => Badge("exited", "backpressure"),
                    ServiceState.Failed => Badge("failed", "down"),
                    _ => Badge("stopped", "backpressure"),
                };
                sb.Append("<tr>");
                sb.Append($"<td><code>{E(p.Name)}</code></td>");
                sb.Append($"<td>{badge}</td>");
                sb.Append($"<td>{(p.StdinOpen ? "open" : "<span class=\"nt\">closed</span>")}</td>");
                sb.Append($"<td>{(p.StartedAt is null ? "<span class=\"nt\">—</span>" : E(Age(p.StartedAt, now)))}</td>");
                sb.Append($"<td>{(p.ExitCode is { } c ? c.ToString() : "<span class=\"nt\">—</span>")}</td>");
                sb.Append($"<td>{(p.ExitedAt is null ? "<span class=\"nt\">—</span>" : E(Age(p.ExitedAt, now)))}</td>");
                sb.Append($"<td><span class=\"mono nt\">{E(ShortId(p.DeclaredByTask))}</span></td>");
                sb.Append("</tr>");
            }

            sb.Append("</tbody></table>");
            // Say the leak out loud: nothing reclaims these when a task ends, by design.
            sb.Append("<p class=\"nt\">A process outlives the task that started it and is never " +
                      "restarted. Nothing stops it automatically — a Lead sends a cleanup task, " +
                      "or it runs until this machine's docketd restarts. Docket tracks no port for " +
                      "a process; reachability is a registered service (§8.2). A process with " +
                      "closed stdin has no graceful stop.</p>");
            sb.Append("</div>");
        }

        static void AppendServices(StringBuilder sb, MachineView m, DateTimeOffset now)
        {
            // §10/§12: exactly what the machine reported on its last heartbeat. The plane
            // stores and renders; it forms no opinion about whether a service is healthy,
            // and there is nothing persisted here to go stale — a disconnected machine
            // drops off this page entirely, services and all.
            if (m.Services is not { Count: > 0 })
                return;

            sb.Append("<div class=\"machine-services\">");
            sb.Append("<h3>Services <span class=\"nt\">declared by the operator on this machine</span></h3>");
            sb.Append("<table><thead><tr>");
            sb.Append("<th>Service</th><th>State</th><th>Port</th><th>Up for</th>" +
                      "<th>Restarts</th><th>Last exit</th><th>Last failure</th>");
            sb.Append("</tr></thead><tbody>");

            foreach (var s in m.Services)
            {
                var badge = s.State switch
                {
                    ServiceState.Running => Badge("running", "ready"),
                    ServiceState.Starting => Badge("starting", "state-submitted"),
                    ServiceState.Failed => Badge("failed", "down"),
                    ServiceState.Disabled => Badge("disabled", "nt"),
                    _ => Badge("stopped", "backpressure"),
                };
                sb.Append("<tr>");
                sb.Append($"<td><code>{E(s.Name)}</code></td>");
                sb.Append($"<td>{badge}</td>");
                sb.Append($"<td>{(s.Port == 0 ? "<span class=\"nt\">n/a</span>" : s.Port.ToString())}</td>");
                sb.Append($"<td>{(s.StartedAt is null ? "<span class=\"nt\">—</span>" : E(Age(s.StartedAt, now)))}</td>");
                sb.Append($"<td>{s.Restarts}</td>");
                sb.Append($"<td>{(s.LastExitCode is { } code ? code.ToString() : "<span class=\"nt\">—</span>")}</td>");
                sb.Append($"<td>{(s.LastFailureAt is null ? "<span class=\"nt\">never</span>" : E(Age(s.LastFailureAt, now)))}</td>");
                sb.Append("</tr>");
            }

            sb.Append("</tbody></table>");
            // Honest about the one thing this view deliberately does not offer, so an
            // operator looks in the right place instead of hunting for a link.
            sb.Append("<p class=\"nt\">Log contents are captured on the machine, under the state " +
                      "directory, and are not served here (§16 open question 8).</p>");
            sb.Append("</div>");
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
        sb.Append("<th>Byte burn</th><th>Last activity</th>");
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
            // Measured, but best-effort (§9.10) — the cell says so when nothing has reported.
            sb.Append($"<td>{ByteBurnCell(t.ForwardUsage)}</td>");
            sb.Append($"<td>{E(Age(t.LastActivity, now))}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></section>");
        return Page("Teams", "teams", sb.ToString());
    }

    // ── Team view (detail) — §4 reattachment surface ──────────────────────────

    /// <summary>One Team in full.</summary>
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
        sb.Append(ByteBurnMetric(team.ForwardUsage));
        sb.Append("</section>");

        sb.Append(MeasuredUsageSection(team));
        sb.Append(RelayBytesSection(team));

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
            sb.Append("<th class=\"num\">Attempt</th><th class=\"num\">Parks</th><th>Detail</th>");
            sb.Append("<th>Result</th><th>Report</th><th>Q&amp;A</th><th>Transcript</th>");
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
                sb.Append($"<td>{ResultCell(t)}</td>");
                sb.Append($"<td>{ReportCell(t)}</td>");
                sb.Append($"<td>{ExchangeCell(t)}</td>");
                sb.Append($"<td>{TranscriptCell(t)}</td>");
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

        // Open input requests (§12), with the typed kind and the worker's own question —
        // a person cannot answer what they cannot read. Escaped verbatim: it is
        // agent-authored text on a human page (§13).
        sb.Append("<section><h2>Open input requests</h2>");
        if (team.OpenInputRequests.Count == 0)
            sb.Append(Empty("Nothing blocked on input."));
        else
        {
            sb.Append("<table><thead><tr><th>Namespace</th><th>Kind</th><th>Blocked</th>" +
                      "<th>Question</th></tr></thead><tbody>");
            foreach (var r in team.OpenInputRequests)
                sb.Append($"<tr><td><code>{E(r.Namespace)}</code></td>" +
                          $"<td>{KindCell(r.Kind)}</td><td>{E(Age(r.BlockedAt, now))}</td>" +
                          $"<td>{QuestionCell(r.Question)}</td></tr>");
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

        // The question's own text belongs here above all: this is the page a person is
        // looking at when they decide what to answer (§12). Escaped verbatim — it is
        // agent-authored content, never markup (§13).
        sb.Append("<section><h2>Open questions</h2>");
        if (inbox.Questions.Count == 0)
            sb.Append(Empty("No open questions."));
        else
        {
            sb.Append("<table><thead><tr><th>Namespace</th><th>Team</th><th>Kind</th><th>Blocked</th>" +
                      "<th>Question</th></tr></thead><tbody>");
            foreach (var q in inbox.Questions)
                sb.Append($"<tr><td><code>{E(q.Namespace)}</code></td><td>{TeamLink(q.TeamId)}</td>" +
                          $"<td>{KindCell(q.Kind)}</td><td>{E(Age(q.BlockedAt, now))}</td>" +
                          $"<td>{QuestionCell(q.Question)}</td></tr>");
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
            sb.Append("<table><thead><tr><th>Namespace</th><th>Team</th><th>Parked on</th>" +
                      "<th>Kind</th><th>Question</th></tr></thead><tbody>");
            foreach (var p in inbox.Parked)
                sb.Append($"<tr><td><code>{E(p.Namespace)}</code></td><td>{TeamLink(p.TeamId)}</td>" +
                          $"<td class=\"mono\">{E(p.ParkMachine ?? "—")}</td>" +
                          $"<td>{KindCell(p.Kind)}</td><td>{QuestionCell(p.Question)}</td></tr>");
            sb.Append("</tbody></table>");
        }
        sb.Append("</section>");

        // The runner's own auth-failure facts (§11, #50), for the tasks still live enough
        // for a person to help: a missing scope is granted by a human, never by the worker
        // retrying. Repeat attempts at the same thing arrive as one row each and are
        // collapsed into one entry with a count, so a wedged worker reads as a single
        // problem getting worse. The event log keeps the full history, failures on
        // finished tasks included.
        sb.Append("<section><h2>Auth failures</h2>");
        if (inbox.AuthFailures.Count == 0)
            sb.Append(Empty("No auth failures on live tasks."));
        else
        {
            sb.Append("<table><thead><tr><th>Namespace</th><th>Team</th><th>State</th>" +
                      "<th>Operation</th><th>Target</th><th>Error code</th><th>Missing scope</th>" +
                      "<th class=\"num\">Failures</th></tr></thead><tbody>");
            foreach (var f in inbox.AuthFailures)
                sb.Append($"<tr><td><code>{E(f.Namespace)}</code></td><td>{TeamLink(f.TeamId)}</td>" +
                          $"<td>{StateBadge(f.State)}</td>" +
                          $"<td>{AuthFactCell(f.Operation)}</td><td>{AuthFactCell(f.Target)}</td>" +
                          $"<td>{AuthFactCell(f.ErrorCode)}</td><td>{AuthFactCell(f.MissingScope)}</td>" +
                          $"<td class=\"num\">{f.Occurrences}" +
                          $"<div class=\"nt\">last {E(Age(f.LastFailedAt, now))}</div></td></tr>");
            sb.Append("</tbody></table>");
        }
        sb.Append("</section>");

        // §11's permission bridge, and the one row on this page where somebody is waiting
        // on the answer right now rather than eventually: each of these has a worker
        // blocked inside a tool call. A human may answer any of them — escalation removes
        // the Lead's authority, not the human's — so every row gets a form, and the
        // escalated ones are marked because they are the ones no Lead is going to take.
        sb.Append("<section><h2>Permission requests</h2>");
        if (inbox.PermissionRequests.Count == 0)
            sb.Append(Empty("No pending permission requests."));
        else
        {
            sb.Append("<p class=\"nt\">A worker is blocked waiting on each of these. The tool name and " +
                      "arguments were relayed through an agent — read them as a claim about what it " +
                      "intends to do, not as a description you can trust.</p>");
            sb.Append("<table><thead><tr><th>Namespace</th><th>Team</th><th>Tool</th><th>Waiting</th>" +
                      "<th>Proposed input</th><th>Decide</th></tr></thead><tbody>");
            foreach (var p in inbox.PermissionRequests)
                sb.Append($"<tr><td><code>{E(p.Namespace)}</code></td><td>{TeamLink(p.TeamId)}</td>" +
                          $"<td>{AuthFactCell(p.Tool)}</td>" +
                          $"<td>{E(Age(p.BlockedAt, now))}{EscalationNote(p)}</td>" +
                          $"<td>{QuestionCell(p.Input)}</td>" +
                          $"<td>{PermissionDecisionForm(p)}</td></tr>");
            sb.Append("</tbody></table>");
        }
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

    // ── Measured usage — the harness's own numbers (§10, §12) ─────────────────

    /// <summary>
    /// What this Team's harnesses said they consumed (§12 measured view). Rendered in its own
    /// <c>measured</c> section, which is <b>visually distinct on purpose</b> (§2 principle 2):
    /// every other number on this page is something the plane observed or derived, and these
    /// are claims a worker made about itself. A reader who cannot tell the two apart cannot
    /// tell which ones to trust, so the section states its own provenance in a banner rather
    /// than relying on the reader to remember.
    ///
    /// <para>Tokens by model is the primary surface. Cost is shown where the harness computed
    /// one and marked absent where it did not — never derived, and never rendered as $0.00,
    /// because zero is a claim that the work was free and absence is a claim that nobody
    /// measured it (<see cref="ModelPricing"/>).</para>
    /// </summary>
    private static string MeasuredUsageSection(TeamDetail team)
    {
        var usage = team.Usage;
        var sb = new StringBuilder();
        sb.Append("<section class=\"measured\"><h2>Measured usage <span class=\"measured-tag\">reported by the harness</span></h2>");
        sb.Append("<p class=\"sub\">Every number in this section was computed by the <strong>worker's own " +
                  "harness</strong> and relayed verbatim — not observed by the control plane, and enforced on " +
                  "by nothing. A harness can under-report, mis-report, or report nothing at all. Treat it as " +
                  "the worker's claim about itself (§2 principle 2, §10).</p>");

        if (usage is not { Measured: true })
        {
            sb.Append(Empty("Nothing reported yet — no harness on this Team has sent a usage report. " +
                            "An absence of measurement, not a measurement of zero."));
            sb.Append("</section>");
            return sb.ToString();
        }

        sb.Append("<table><thead><tr><th>Model</th>");
        sb.Append("<th class=\"num\">Input</th><th class=\"num\">Output</th>");
        sb.Append("<th class=\"num\">Cache read</th><th class=\"num\">Cache write</th>");
        sb.Append("<th class=\"num\">Total</th><th class=\"num\">Cost</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var m in usage.ByModel)
        {
            sb.Append("<tr>");
            sb.Append($"<td>{(m.Model is { Length: > 0 } name
                ? $"<code>{E(name)}</code>"
                : "<span class=\"nt\" title=\"this harness names no model; the tokens are still real\">not reported</span>")}</td>");
            sb.Append($"<td class=\"num\">{E(Tokens(m.InputTokens))}</td>");
            sb.Append($"<td class=\"num\">{E(Tokens(m.OutputTokens))}</td>");
            sb.Append($"<td class=\"num\">{E(Tokens(m.CacheReadTokens))}</td>");
            sb.Append($"<td class=\"num\">{E(Tokens(m.CacheWriteTokens))}</td>");
            sb.Append($"<td class=\"num\">{E(Tokens(m.TotalTokens))}</td>");
            sb.Append($"<td class=\"num\">{CostCell(m.CostUsd, m.CostProvenance)}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody><tfoot><tr>");
        sb.Append("<td>All models</td>");
        sb.Append($"<td class=\"num\">{E(Tokens(usage.InputTokens))}</td>");
        sb.Append($"<td class=\"num\">{E(Tokens(usage.OutputTokens))}</td>");
        sb.Append($"<td class=\"num\">{E(Tokens(usage.CacheReadTokens))}</td>");
        sb.Append($"<td class=\"num\">{E(Tokens(usage.CacheWriteTokens))}</td>");
        sb.Append($"<td class=\"num\">{E(Tokens(usage.TotalTokens))}</td>");
        sb.Append($"<td class=\"num\">{CostCell(usage.ReportedCostUsd, usage.ReportedCostUsd is null ? UsageCostProvenance.None : UsageCostProvenance.Reported)}</td>");
        sb.Append("</tr></tfoot></table>");

        // A reasoning breakdown is Codex-only, so it renders only where one arrived — and says
        // that it is already inside the output column rather than sitting beside it.
        var reasoning = usage.ByModel.Where(m => m.ReasoningOutputTokens is > 0).ToList();
        if (reasoning.Count > 0)
            sb.Append("<p class=\"nt\">Of the output above, " +
                      E(Tokens(reasoning.Sum(m => m.ReasoningOutputTokens!.Value))) +
                      " tokens were reported as reasoning — a portion <em>of</em> output, not an addition to it. " +
                      "Only some harnesses break this out.</p>");

        if (usage.CostIsPartial)
            sb.Append("<p class=\"nt\">The cost total covers only the models that reported one, so it is a " +
                      "<strong>floor, not a total</strong>. A harness that reports tokens but no dollars leaves " +
                      "its cost blank here; nothing multiplies tokens into a price to fill the gap.</p>");

        sb.Append($"<p class=\"nt\">Last report {E(usage.ReportedAt?.ToString("u", CultureInfo.InvariantCulture) ?? "—")}. " +
                  "Reports are cumulative and best-effort: this trails a live worker by up to one report, and a " +
                  "worker killed before its last one leaves that tail uncounted.</p>");
        sb.Append("</section>");
        return sb.ToString();
    }

    /// <summary>
    /// A cost cell, carrying its provenance to the pixel. The three cases must not look alike:
    /// a reported figure is the harness's arithmetic, a derived one would be Docket's, and an
    /// absent one is neither — and rendering the third as "$0.00" would turn "nobody measured
    /// this" into "this was free".
    /// </summary>
    private static string CostCell(decimal? cost, UsageCostProvenance provenance) =>
        (cost, provenance) switch
        {
            ({ } c, UsageCostProvenance.Reported) => $"{E(Usd(c))} <span class=\"nt\">USD</span>",
            ({ } c, UsageCostProvenance.Derived) =>
                $"<span title=\"derived by Docket from token counts, not reported by the harness\">~{E(Usd(c))} "
                + "<span class=\"nt\">USD est.</span></span>",
            _ => "<span class=\"nt\" title=\"this harness reports no cost\">not reported</span>",
        };

    /// <summary>A token count with thousands separators — these run to millions and an
    /// unseparated run of digits is unreadable at a glance.</summary>
    private static string Tokens(long count) => count.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A USD amount, invariant so the rendered number never depends on the server's
    /// locale. Four decimals because a single cheap task legitimately costs fractions of a
    /// cent, and rounding those to 0.00 would make the column look broken.</summary>
    private static string Usd(decimal amount) => amount.ToString("0.####", CultureInfo.InvariantCulture);

    // ── Relay bytes (§9.10) ───────────────────────────────────────────────────

    /// <summary>
    /// A byte count in the largest unit that keeps it readable. Binary units, because these are
    /// bytes moved through a socket, and 1024-based is what every other tool an operator will
    /// compare this against reports.
    /// </summary>
    private static string Bytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes} B"
            : $"{value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    /// <summary>
    /// Relay bytes a Team has moved (§9.10). An unmeasured Team reads "not reported" rather than
    /// "0 B": no relay has spoken, which is an absence of data and not a measurement of no
    /// traffic — the distinction an operator needs before concluding a Team is quiet.
    /// </summary>
    private static string ByteBurnCell(TeamForwardUsageView? usage) =>
        usage is { Measured: true }
            ? E(Bytes(usage.ForwardedBytes))
            : "<span class=\"nt\" title=\"no relay has reported\">not reported</span>";

    /// <summary>
    /// The relay-bytes line (§9.10). Says two things an operator has to read off it: this
    /// figure is best-effort, so it trails live traffic and loses a dead relay's tail, and
    /// <em>nothing is enforced on it</em> — §8.3 forbids cutting an established splice, so
    /// there is no byte ceiling to reach. The report age is shown rather than implied.
    /// </summary>
    private static string RelayBytesNote(TeamForwardUsageView? usage) =>
        usage is { Measured: true }
            ? $"<p class=\"nt\">Relay bytes forwarded: <strong>{E(Bytes(usage.ForwardedBytes))}</strong>, " +
              $"last reported {E(usage.ReportedAt?.ToString("u", CultureInfo.InvariantCulture) ?? "—")}. " +
              "Best-effort: the relay reports periodically, so this trails live traffic and a relay that " +
              "dies loses its unsent tail. Nothing is enforced on it; an established splice is never cut " +
              "mid-flight (§8.3, §9 check 10).</p>"
            : "<p class=\"nt\">Relay bytes forwarded: not reported — no relay has sent a count for this Team. " +
              "An absence of measurement, not a measurement of zero (§9 check 10).</p>";

    /// <summary>The byte-burn slot in the Team detail metrics strip (§9.10).</summary>
    private static string ByteBurnMetric(TeamForwardUsageView? usage) =>
        usage is { Measured: true }
            ? $"<div class=\"metric\"><div class=\"n\">{E(Bytes(usage.ForwardedBytes))}</div>" +
              "<div class=\"l\">byte burn</div></div>"
            : "<div class=\"metric\"><div class=\"n nt\">n/a</div><div class=\"l\">byte burn</div></div>";

    /// <summary>
    /// The Team's relay-byte section (§9.10). Its own section rather than a line under the
    /// metrics strip, because the tile can show the number but not the two things that make
    /// it readable: how stale it is, and that nothing is enforced on it.
    /// </summary>
    private static string RelayBytesSection(TeamDetail team)
    {
        var sb = new StringBuilder();
        sb.Append("<section><h2>Relay bytes</h2>");
        sb.Append(RelayBytesNote(team.ForwardUsage));
        sb.Append("</section>");
        return sb.ToString();
    }

    /// <summary>
    /// The allow/deny form on one pending permission request (§11/§12): a plain POST carrying
    /// the task id in a hidden field, with the human's authority coming from their session
    /// rather than from anything this markup asserts. Two submit buttons rather than a select,
    /// because the two outcomes are not interchangeable and one of them needs the message
    /// beside it to be worth anything.
    /// </summary>
    private static string PermissionDecisionForm(PermissionRequestView p)
    {
        var sb = new StringBuilder();
        sb.Append("<form class=\"permission-decide\" method=\"post\" action=\"/dashboard/permission\">");
        sb.Append($"<input type=\"hidden\" name=\"taskId\" value=\"{E(p.TaskId.ToString())}\">");
        sb.Append("<input type=\"text\" name=\"message\" placeholder=\"message to the worker\" " +
                  "aria-label=\"message to the worker\">");
        sb.Append("<button type=\"submit\" name=\"verdict\" value=\"allow\">Allow</button>");
        sb.Append("<button type=\"submit\" name=\"verdict\" value=\"deny\">Deny</button>");
        sb.Append("<div class=\"nt\">A denial needs a message — the worker reads it and adapts, " +
                  "so say what to do instead.</div>");
        sb.Append("</form>");
        return sb.ToString();
    }

    /// <summary>
    /// Whether a Lead has handed this request over, and what it said when it did (§11). Shown
    /// beside the age because both answer the same question — how much attention this needs
    /// now — and because a request no Lead will touch is one whose wait TTL is running down
    /// with nobody on it. The reason is Lead-authored text, escaped like every other agent
    /// string on this page.
    /// </summary>
    private static string EscalationNote(PermissionRequestView p) =>
        p.EscalatedAt is null
            ? ""
            : $"<div class=\"nt\">{Badge("escalated", "state-blockedoninput")} " +
              $"{E(p.EscalationReason ?? "no reason recorded")}</div>";

    /// <summary>The result page after a human decides a permission request (§11/§12).</summary>
    public static string PermissionDecided(PermissionRequestView p)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>Permission decided</h1>");
        sb.Append($"<p class=\"sub mono\">{E(p.Namespace)}</p>");
        sb.Append("<section>");
        sb.Append($"<p><code>{E(p.Tool ?? "unnamed tool")}</code> was " +
                  $"<strong>{E(p.Verdict?.ToString().ToLowerInvariant() ?? "not decided")}</strong>.</p>");
        if (p.Message is { Length: > 0 } m)
            sb.Append($"<p>The worker was told:</p><pre class=\"report\">{E(m)}</pre>");
        sb.Append("<p class=\"nt\">The worker was waiting on this and resumes now — it was never " +
                  "requeued, so there is no redispatch to wait for.</p>");
        sb.Append("<p><a href=\"/dashboard/inbox\">Back to the inbox</a></p>");
        sb.Append("</section>");
        return Page("Permission", "inbox", sb.ToString(), autoRefresh: false);
    }

    /// <summary>
    /// Shown when the permission write is refused (§11/§12) — a Lead session reaching for the
    /// human's form, or a request that stopped being pending while the page sat open (its
    /// worker gave up, the sweeper parked it, another answerer got there first). Both are
    /// ordinary on a page that auto-refreshes into a decision someone else may also be
    /// making, so this says which happened rather than just failing.
    /// </summary>
    public static string PermissionRefused(string reason)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>Not decided</h1>");
        sb.Append("<section>");
        sb.Append($"<p>{E(reason)}</p>");
        sb.Append("<p><a href=\"/dashboard/inbox\">Back to the inbox</a></p>");
        sb.Append("</section>");
        return Page("Permission", "inbox", sb.ToString(), autoRefresh: false);
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
    /// The §12 transcript link, offered only for a terminal task. A task that can still run
    /// is shown as not-yet-readable rather than linked-and-then-refused: the rule is a
    /// security one (a live worker credential may sit in an in-flight transcript, §13), so
    /// saying so up front beats a 409 the operator has to interpret.
    /// </summary>
    private static string TranscriptCell(TeamTaskView t) =>
        t.State.IsTerminal()
            ? $"<a href=\"/dashboard/tasks/{t.TaskId}/transcripts\">transcript</a>"
            : "<span class=\"nt\" title=\"readable once the task reaches a terminal state (§12)\">not yet</span>";

    /// <summary>
    /// The worker's in-band report (§10), rendered verbatim-escaped behind a
    /// disclosure so the task table stays compact. It is agent-authored text (§13):
    /// escaped through <see cref="E(string)"/> and never interpreted, only shown.
    /// </summary>
    private static string ReportCell(TeamTaskView t) =>
        t.Report is { Length: > 0 } r
            ? $"<details><summary>report</summary><pre class=\"report\">{E(r)}</pre></details>"
            : "<span class=\"nt\">—</span>";

    /// <summary>
    /// The §8.1 result reference a worker handed over on working → verifying (#81) — the
    /// artifact pointer a human adjudicating in <c>review</c> mode rules on, and the one
    /// thing a worker that left no report still said. Agent-authored, so escaped through
    /// <see cref="E(string)"/> and shown as text, deliberately <b>not</b> as a link: the plane has
    /// never dereferenced it (§8.1) and a clickable agent-controlled URL on an operator's
    /// page is a §13 hazard, not a convenience. An em dash means nothing reported yet.
    /// </summary>
    private static string ResultCell(TeamTaskView t) =>
        t.ResultReference is { Length: > 0 } r
            ? $"<code>{E(r)}</code>"
            : "<span class=\"nt\">—</span>";

    /// <summary>
    /// A task's input exchange (§11) behind a disclosure: what the worker asked and what
    /// it was answered. Both are agent- and human-authored free text on a human page, so
    /// both go through <see cref="E(string)"/> verbatim — never rendered as markup (§12/§13).
    /// An open question shows as unanswered, which is the actionable state.
    /// </summary>
    private static string ExchangeCell(TeamTaskView t)
    {
        if (t.Question is not { Length: > 0 } question)
            return "<span class=\"nt\">—</span>";

        var answer = t.Answer is { Length: > 0 } a
            ? $"<pre class=\"report\">{E(a)}</pre>"
            : "<p class=\"nt\">Not yet answered.</p>";
        return $"<details><summary>{E(KindText(t.InputKind))}</summary>" +
               $"<pre class=\"report\">{E(question)}</pre>{answer}</details>";
    }

    /// <summary>The typed input-request kind for a table cell, honest when a row predates
    /// the column (blocked before the kind was persisted on the task).</summary>
    private static string KindCell(Docket.Core.InputRequestKind? kind) =>
        kind is null
            ? "<span class=\"nt\">not recorded</span>"
            : $"<code>{E(KindText(kind))}</code>";

    private static string KindText(Docket.Core.InputRequestKind? kind) =>
        kind?.ToString().ToLowerInvariant() ?? "kind not recorded";

    /// <summary>
    /// The worker's question inline in the inbox and Team lists — the text a person
    /// reads before answering, escaped verbatim (§12/§13). A request that carried no
    /// question says so: answering blind is a different situation from answering.
    /// </summary>
    private static string QuestionCell(string? question) =>
        question is { Length: > 0 } q
            ? $"<pre class=\"report\">{E(q)}</pre>"
            : "<span class=\"nt\">none given</span>";

    /// <summary>
    /// One structured fact of an auth failure in the inbox (§11, #50) — the operation, its
    /// target, the error code, the scope that was missing. A runner reports what it knows
    /// and no more, so a fact it never sent says so instead of leaving a blank cell a
    /// reader would take for a value.
    /// </summary>
    private static string AuthFactCell(string? value) =>
        value is { Length: > 0 } v
            ? $"<code>{E(v)}</code>"
            : "<span class=\"nt\">not reported</span>";

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
        // §9 check 7: a canceled task whose infrastructure requeues reached its cap was
        // abandoned by the plane, not called off by a person — say so, and say what kept
        // failing, since that is the whole operator signal issue #73 asked for.
        else if (t.State == TaskState.Canceled
                 && t.InfrastructureRequeueLimit > 0
                 && t.InfrastructureRequeues >= t.InfrastructureRequeueLimit)
            parts.Add($"<span class=\"nt\">abandoned after {t.InfrastructureRequeues} requeues, " +
                      $"last {E(t.LastRequeueReason?.ToString() ?? "unknown")}</span>");
        // Below the cap, a task that has been requeued at all is worth naming: the count
        // climbing is how a wedge looks before it becomes terminal.
        else if (t.InfrastructureRequeues > 0)
            parts.Add($"<span class=\"nt\">{t.InfrastructureRequeues} requeues" +
                      (t.LastRequeueReason is { } why ? $", last {E(why.ToString())}" : "") + "</span>");
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
    /// falls back to its effect-name detail. All values pass through <see cref="E(string)"/>.
    /// </summary>
    private static string EventDetail(DashboardEvent e)
    {
        // §11/§12 permission audit, checked first because it is the most specific: the
        // verdict, who had the authority to give it, and the words the worker was given.
        // "A Lead approved this" and "a person approved this" are different facts and the
        // log has to be able to tell them apart after the event.
        if (e.PermissionVerdict is { } verdict)
        {
            var who = e.PermissionAnswerer is { } answerer
                ? $"<span class=\"nt\">by {E(answerer.ToString().ToLowerInvariant())}</span>"
                : "<span class=\"nt\">by an unrecorded answerer</span>";
            var said = string.IsNullOrEmpty(e.Detail) ? "" : $" <span class=\"nt\">{E(e.Detail)}</span>";
            return Badge(verdict.ToString().ToLowerInvariant(),
                       verdict == Docket.Core.PermissionVerdict.Allow ? "state-working" : "state-rejected")
                   + $" {who}{said}";
        }

        if (e.InputKind is { } kind)
            return Badge(kind.ToString(), "state-blockedoninput");

        // §6/§9 check 7 (#73): which signal drove this requeue, and — when the requeue
        // was the one that reached the cap — that the task was abandoned rather than
        // redispatched. Before this the log showed a column of identical rows.
        if (e.LivenessReason is { } reason)
            return Badge(reason.ToString(), "state-submitted") +
                   (e.ToState == TaskState.Canceled
                       ? " <span class=\"nt\">requeue cap reached — abandoned</span>"
                       : "");

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
