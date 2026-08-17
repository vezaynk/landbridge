using System.Text;
using Docket.ControlPlane;
using Docket.Core;
using static Docket.Mcp.Dashboard.DashboardHtml;

namespace Docket.Mcp.Dashboard;

/// <summary>
/// The §12 transcript pages, in the same server-rendered style as
/// <see cref="DashboardRenderer"/>. Transcript <em>content</em> is never rendered here — the
/// index only links to the raw <c>text/plain</c> stream — so nothing on this page can turn
/// attacker-influenced bytes into markup.
/// </summary>
internal static class DashboardTranscriptRenderer
{
    /// <summary>
    /// The index of what is readable for one task. Rendered with <c>autoRefresh: false</c>:
    /// every other §12 view has a 5s meta-refresh, which here would re-ask each machine for
    /// an inventory every five seconds over the control channel that also carries dispatch
    /// and liveness.
    /// </summary>
    public static string Index(
        SessionId task,
        IReadOnlyList<TranscriptLocationView> locations,
        IReadOnlyList<TranscriptMachineInventory> inventories,
        DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append($"<h1>Transcripts · session <code>{E(ShortId(task.Value))}</code></h1>");
        sb.Append($"<p class=\"sub mono\">{E(task.Value)}</p>");
        sb.Append(WarningBanner());

        sb.Append("<section><h2>Dispatches</h2>");
        if (locations.Count == 0)
            sb.Append(Empty("This task was never dispatched, so nothing was ever captured."));
        else
        {
            sb.Append("<table><thead><tr><th>Dispatched</th><th>Machine</th><th>Readable now</th></tr></thead><tbody>");
            foreach (var l in locations)
            {
                var machine = l.Machine is null
                    ? NotTracked("machine")
                    : $"<code>{E(l.Machine)}</code>";
                var readable = l switch
                {
                    { Machine: null } => "not recorded for this attempt",
                    { Connected: true } => Badge("machine connected", "state-working"),
                    _ => Badge("machine offline", "state-parked"),
                };
                sb.Append($"<tr><td>{E(Age(l.DispatchedAt, now))}</td><td>{machine}</td><td>{readable}</td></tr>");
            }
            sb.Append("</tbody></table>");
        }
        sb.Append("</section>");

        sb.Append("<section><h2>Captured streams</h2>");
        if (inventories.Count == 0)
        {
            sb.Append(Empty(
                "No machine that ran this task is connected. Transcripts live only on the machine that " +
                "captured them, so there is nothing to read until one reconnects."));
        }
        foreach (var inv in inventories)
        {
            sb.Append($"<h3>Machine <code>{E(inv.Machine)}</code></h3>");
            if (inv.Unavailable is { } why)
            {
                sb.Append(Empty(why));
                continue;
            }
            if (inv.Instances.Count == 0)
            {
                sb.Append(Empty(
                    "This machine ran the task but captured nothing — transcript capture is off for its " +
                    "profile, or the worker wrote no output."));
                continue;
            }

            sb.Append("<table><thead><tr>");
            sb.Append("<th class=\"num\">Instance</th><th>Last write</th><th>stdout</th><th>stderr</th>");
            sb.Append("</tr></thead><tbody>");
            foreach (var i in inv.Instances)
            {
                sb.Append($"<tr><td class=\"num\">{i.Ordinal:D4}</td><td>{E(Age(i.LastWrite, now))}</td>");
                sb.Append($"<td>{StreamCell(task, inv.Machine, i.Ordinal, "stdout", i.StdoutBytes)}</td>");
                sb.Append($"<td>{StreamCell(task, inv.Machine, i.Ordinal, "stderr", i.StderrBytes)}</td>");
                sb.Append("</tr>");
            }
            sb.Append("</tbody></table>");
        }
        sb.Append("</section>");

        return Page($"Transcripts · {ShortId(task.Value)}", "teams", sb.ToString(), autoRefresh: false);
    }

    /// <summary>A link to the raw stream, or a dash when that stream captured nothing.</summary>
    private static string StreamCell(SessionId task, string machine, int ordinal, string stream, long bytes)
    {
        if (bytes == 0)
            return "—";
        // &amp; in an attribute, not a bare & — the rest of this dashboard escapes everything
        // it emits, and a query string is no exception.
        var href = $"/dashboard/sessions/{task.Value}/transcript" +
                   $"?machine={Uri.EscapeDataString(machine)}&amp;ordinal={ordinal}&amp;stream={stream}";
        return $"<a href=\"{href}\">{E(Bytes(bytes))}</a>";
    }

    /// <summary>Why a read produced nothing, in the operator's terms.</summary>
    public static string Unavailable(TranscriptResult.Unavailable unavailable)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>Transcript unavailable</h1>");
        sb.Append($"<p class=\"empty\">{E(unavailable.Detail)}</p>");
        if (unavailable.Reason == TranscriptUnavailable.NotTerminal)
        {
            // Say why the rule exists, not just that it exists: an operator who understands
            // it will not file a bug, and the honest reason is a security one (§13).
            sb.Append("<p class=\"sub\">Transcripts are served verbatim and are not redacted, so Docket " +
                      "serves them only for tasks that can never run again — a task still in flight may " +
                      "have a live worker credential in its transcript.</p>");
        }
        return Page("Transcript unavailable", "teams", sb.ToString(), autoRefresh: false);
    }

    /// <summary>The Lead-token refusal — the one dashboard route that will not accept one.</summary>
    public static string LeadRefused()
    {
        var sb = new StringBuilder();
        sb.Append("<h1>Transcripts are a human surface</h1>");
        sb.Append("<p class=\"empty\">This session is a Lead credential. Transcripts are served only to a " +
                  "human operator session.</p>");
        sb.Append("<p class=\"sub\">A full transcript is unbounded, unredacted, untrusted text carrying " +
                  "everything the agent read — far beyond the bounded worker report a Lead reads with " +
                  "<code>get_session_report</code>. Sign in as an operator to read it.</p>");
        return Page("Transcripts", "teams", sb.ToString(), autoRefresh: false);
    }

    /// <summary>The standing warning, shown wherever a transcript can be reached.</summary>
    private static string WarningBanner() =>
        "<section class=\"metrics\"><div class=\"metric\"><div class=\"l\">" +
        E("Raw harness output, served verbatim. It may contain credentials, customer data, or anything " +
          "else the agent read or printed. Docket does not redact transcripts (§13). Do not paste it " +
          "into a ticket, a chat, or another agent.") +
        "</div></div></section>";

    /// <summary>Human-readable byte size, so an operator can tell a 2 KB stub from a 40 MB run.</summary>
    private static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):N1} MB",
    };
}
