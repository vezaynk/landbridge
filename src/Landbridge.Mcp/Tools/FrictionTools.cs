using System.ComponentModel;
using System.Text;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Landbridge.Mcp.Auth;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Landbridge.Mcp.Tools;

/// <summary>
/// Product-feedback surface for both Leads and workers. The plane stores the
/// message verbatim; operators read it on the dashboard Friction tab. Not a
/// session transition — a Lead or worker may call it at any time.
/// </summary>
[McpServerToolType]
public sealed class FrictionTools(FrictionStore store, TokenService tokens, FriendlyIds ids, IHttpContextAccessor http)
{
    [McpServerTool(Name = "report_friction"),
     Description("Report friction in Landbridge itself — a missing tool, a confusing refusal, " +
                 "an awkward loop, a gap in the skills. NOT about the session's work (that is " +
                 "report_result or request_input). Say what happened and how it could be better. " +
                 "Operators read these on the dashboard. Capped at 16 KB; over-cap is refused. " +
                 "A Lead must pass teamId (from create_team or a human).")]
    public async Task<string> ReportFriction(
        [Description("What felt wrong in Landbridge and how it could be improved. Specific: which " +
                     "tool or loop, what you expected, what happened. Not the assignment. Capped at 16 KB.")]
        string message,
        [Description("Lead only: the Team this report is about. From create_team, or a human-supplied id. " +
                     "Workers omit this — their token already names the Team.")]
        string? teamId = null,
        CancellationToken ct = default)
    {
        var user = http.HttpContext?.User ?? throw Unauthorized();
        if (LandbridgeClaims.AsEvictedLead(user) is { } evicted)
        {
            throw new McpException(
                $"your lead claim on team {evicted.Team.Value:N} was taken over by human " +
                $"{evicted.EvictedByHuman:N} at {evicted.EvictedAt:O}; reattach to the Team to continue.");
        }

        string role;
        Guid team;
        Guid? sessionId;
        Guid? humanId;
        if (LandbridgeClaims.AsLeadPrincipal(user) is { } lead)
        {
            var owned = await ids.TryTeamAsync(teamId, ct);
            if (owned is null || !await tokens.OwnsTeamAsync(lead.CredentialId, owned.Value, ct))
            {
                throw new McpException(
                    "teamId is required for a Lead and must be a team this credential owns; " +
                    "create_team or use a team id you were given.");
            }
            role = FrictionReportRow.LeadRole;
            team = owned.Value.Value;
            sessionId = null;
            humanId = lead.HumanId;
        }
        else if (LandbridgeClaims.AsWorker(user) is { } worker)
        {
            role = FrictionReportRow.WorkerRole;
            team = worker.Team.Value;
            sessionId = worker.Session.Value;
            humanId = null;
        }
        else
        {
            throw Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new McpException(
                "message is required: say what friction you felt in Landbridge and how it could be improved");
        }

        if (Encoding.UTF8.GetByteCount(message) > FrictionStore.MaxMessageBytes)
        {
            throw new McpException(
                $"message is over the {FrictionStore.MaxMessageBytes / 1024} KB cap; shorten it");
        }

        await store.RecordAsync(role, team, sessionId, humanId, message, ct);
        return "ok: friction recorded";
    }

    private static McpException Unauthorized() =>
        new("this tool requires a live lead claim or a dispatched worker");
}
