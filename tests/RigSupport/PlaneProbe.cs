using System.Net;
using System.Net.Sockets;
using System.Text;
using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
using Docket.Core;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Docket.RigSupport;

/// <summary>
/// The plane-facing half of a fleet rig: reserving a loopback port, polling committed
/// control-plane state, driving the real Lead MCP surface, and rendering a task's committed
/// facts for a timeout message. Shared by <c>Docket.MultiMachine.Tests</c>' <c>FleetRig</c>
/// and <c>Docket.Chaos.Tests</c>' <c>ChaosFleet</c>, which had grown their own copy of each.
///
/// <para><b>Why a source-only directory rather than a project.</b> This is linked into both
/// suites with <c>&lt;Compile Include="../RigSupport/PlaneProbe.cs" Link="..." /&gt;</c>, the same
/// idiom <c>tests/HarnessSupport/ParentDeathSignal.cs</c> already uses for the harness dead-man
/// convention: no csproj, not in the solution, compiled into each consumer. The alternative —
/// hosting it in <c>Docket.ControlPlane.Tests</c> and source-linking from there — would have
/// forced an MCP client package onto a suite that never speaks MCP, since a linked file must
/// compile in its home project too. Sharing test source should not add a dependency to a
/// project that does not want one.</para>
///
/// <para><b>What deliberately stays in each rig.</b> The two rigs are not variants of one
/// thing and this type does not try to unify them. <c>FleetRig</c> runs the plane, the relay,
/// and N <c>ProcessSupervisor</c>s <em>in-process</em>, standing in for the §10 socket with a
/// registry send delegate; <c>ChaosFleet</c> runs the plane and <c>docketd</c> as separate OS
/// processes over the real <c>/runner</c> WebSocket, precisely so they can be SIGKILLed. Their
/// csprojs prove the split is structural rather than stylistic: Chaos references
/// <c>Docket.Runner</c> with <c>ReferenceOutputAssembly="false"</c>, keeping runner types off
/// its compile closure entirely, so nothing here may touch <c>Docket.Runner</c>,
/// <c>Docket.Mcp</c>, or <c>Docket.Relay</c>. Bring-up, teardown, machine enrollment, and every
/// rig-specific diagnostic addition stay where they are.</para>
///
/// <para><b>Failures throw with the reason.</b> Where a rig previously asserted
/// <c>Assert.NotEqual(true, result.IsError)</c> — which reports "values are equal" and none of
/// the tool's own complaint — these helpers throw carrying the error content, so a refused
/// <c>create_session</c> says why in the first line of the failure.</para>
/// </summary>
internal static class PlaneProbe
{
    // ── Loopback reservation ────────────────────────────────────────────────────

    /// <summary>
    /// Reserve a free loopback URL by binding an ephemeral port and releasing it, for the
    /// case where an address must be <em>configured</em> before the thing that binds it
    /// exists — the plane is told its own public URL at startup because that is what it
    /// writes into every worker's generated MCP config (§8.3).
    /// </summary>
    public static string ReserveLoopbackUrl() => $"http://127.0.0.1:{ReserveLoopbackPort()}";

    /// <summary>
    /// A free loopback port, found by binding an ephemeral port and releasing it. Also used
    /// where a port has to be known before a listener exists at all: a §10 process declares
    /// no port, so an agent told to start one has to be told which, and the test cannot ask
    /// the process afterwards.
    /// </summary>
    public static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // ── Bounded polling ────────────────────────────────────────────────────────

    /// <summary>
    /// Bounded poll: true as soon as <paramref name="condition"/> holds, false at the
    /// deadline. <b>The poll IS the synchronization</b> — no rig sleeps for a fixed duration
    /// and hopes, because a fixed sleep either flakes on a slow box or wastes the difference
    /// on a fast one. The condition is re-evaluated once more at the deadline so a predicate
    /// that became true in the final interval is not reported as a timeout.
    /// </summary>
    public static async Task<bool> WaitUntilAsync(
        Func<Task<bool>> condition, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await condition())
                return true;
            await Task.Delay(100, ct);
        }
        return await condition();
    }

    // ── The real Lead MCP surface ──────────────────────────────────────────────

    /// <summary>
    /// Connects to the plane's real MCP endpoint with <paramref name="bearer"/>. Also used to
    /// replay a credential that is <em>expected</em> to be refused (§17.8's stale
    /// worker-instance token), so it must not assert anything about the outcome.
    /// </summary>
    public static async Task<McpClient> ConnectMcpAsync(Uri endpoint, string bearer, CancellationToken ct)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearer}" },
        });
        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    /// <summary>
    /// A live Lead token for <paramref name="team"/>, minted straight against the store the
    /// way an OAuth callback would (§5): a human session, then the lead-claim flow. Lead
    /// identity <em>is</em> the credential, so there is no tool to call for this.
    /// </summary>
    public static async Task<string> LeadTokenAsync(DocketDbContext db, TeamId team, CancellationToken ct)
    {
        var tokens = new TokenService(db, TimeProvider.System);
        var human = await tokens.IssueHumanSessionAsync(ct);
        var claim = (LeadClaimResult.Claimed)await tokens.ClaimLeadAsync(human.Token, team, ct: ct);
        return claim.Token.Token;
    }

    /// <summary>
    /// Create a task over the real Lead MCP surface and return its id.
    /// </summary>
    /// <param name="profile">
    /// §10 exact-match profile routing, or null for <c>default</c>. A rig that declares a
    /// second profile (a wedged-worker archetype, say) selects it here.
    /// </param>
    /// <param name="continues">
    /// §11 continuation: the prior task whose agent session this one continues. Seeds the new
    /// row's session ref and preferred machine, so its first dispatch prefers the machine
    /// holding the transcript.
    /// </param>
    public static async Task<SessionId> CreateSessionAsync(
        McpClient lead,
        string description,
        string completionCriteria,
        string workspace,
        CancellationToken ct,
        string? profile = null,
        SessionId? continues = null)
    {
        var created = await lead.CallToolAsync("create_session", new Dictionary<string, object?>
        {
            ["description"] = description,
            ["completionCriteria"] = completionCriteria,
            ["mode"] = "lead",
            ["profile"] = profile,
            ["workspace"] = workspace,
            ["continues"] = continues?.Value.ToString(),
        }, cancellationToken: ct);

        var text = TextOf(created);
        if (created.IsError == true)
            throw new InvalidOperationException($"create_session was refused: {text}");
        return new SessionId(Guid.Parse(text));
    }

    /// <summary>
    /// Accept a <c>verifying</c> task as the Lead, driving it to <c>completed</c> (§7) — the
    /// terminal state a transcript becomes readable in (§12).
    /// </summary>
    public static async Task AcceptAsync(McpClient lead, SessionId task, CancellationToken ct)
    {
        var verdict = await lead.CallToolAsync("submit_review", new Dictionary<string, object?>
        {
            ["sessionId"] = task.Value.ToString(),
            ["verdict"] = "accept",
        }, cancellationToken: ct);

        if (verdict.IsError == true)
            throw new InvalidOperationException($"submit_review was refused: {TextOf(verdict)}");
    }

    /// <summary>Every text block of a tool result, concatenated — the payload and, on a
    /// refusal, the reason.</summary>
    private static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    // ── Committed control-plane state ──────────────────────────────────────────

    /// <summary>The task's committed state, or null when there is no such row.</summary>
    public static Task<SessionState?> StateAsync(DocketDbContext db, SessionId task, CancellationToken ct) =>
        new SessionStore(db, TimeProvider.System).GetStateAsync(task, ct);

    /// <summary>
    /// Append one task's committed facts to <paramref name="sb"/>: the row, its worker
    /// instances, and its ordered event log. This is the part of a timeout dump both rigs
    /// were writing separately, and it is the part that answers the question a CI failure
    /// actually poses — which transitions happened, in what order, and what the plane
    /// believes now.
    ///
    /// <para><paramref name="prefix"/> opens every line, so each rig keeps its own framing
    /// (<c>ChaosFleet</c> draws a box, <c>FleetRig</c> indents) and adds its own sections
    /// around this one. Two substrings here are load-bearing beyond readability:
    /// <c>state=&lt;X&gt;</c> and the <c>From-&gt;To</c> transition shape are asserted by
    /// <c>RealClaudeCollaborationTests.Timeout_diagnostics_render_the_plane_state_for_a_stuck_worker</c>,
    /// a self-test on the diagnostic itself — a dump that quietly stops rendering is worth
    /// nothing at the moment it is needed.</para>
    /// </summary>
    public static async Task AppendTaskFactsAsync(
        StringBuilder sb, DocketDbContext db, SessionId task, string prefix, CancellationToken ct)
    {
        var row = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == task.Value, ct);
        if (row is null)
        {
            sb.AppendLine($"{prefix}{task}: (no row)");
            return;
        }

        sb.AppendLine(
            $"{prefix}{task}: state={row.State} attempt={row.Attempt} " +
            $"infraRequeues={row.InfrastructureRequeues}/{row.InfrastructureRequeueLimit} " +
            $"lastRequeueReason={row.LastRequeueReason?.ToString() ?? "(none)"} " +
            $"verificationFailures={row.VerificationFailures} " +
            $"profile={row.Profile ?? "(default)"} result={row.ResultReference ?? "(none)"} " +
            $"instance={row.CurrentInstanceId?.ToString() ?? "(none)"} " +
            $"inputKind={row.InputKind?.ToString() ?? "(none)"}");

        var instances = await db.WorkerInstances.AsNoTracking()
            .Where(w => w.SessionId == task.Value)
            .OrderBy(w => w.CreatedAt)
            .ToListAsync(ct);
        sb.AppendLine($"{prefix}  instances: " + (instances.Count == 0
            ? "(none)"
            : string.Join(", ", instances.Select(i => $"{i.Id.ToString()[..8]}{(i.Revoked ? " revoked" : " LIVE")}"))));

        var events = await db.SessionEvents.AsNoTracking()
            .Where(e => e.SessionId == task.Value)
            .OrderBy(e => e.OccurredAt)
            .ToListAsync(ct);
        sb.AppendLine($"{prefix}  events ({events.Count}):");
        foreach (var e in events)
        {
            sb.AppendLine(
                $"{prefix}    {e.OccurredAt:HH:mm:ss.fff} {e.Kind} " +
                $"{e.FromState?.ToString() ?? "-"}->{e.ToState?.ToString() ?? "-"}" +
                (e.LivenessReason is { } reason ? $" reason={reason}" : "") +
                (e.Detail is { Length: > 0 } d ? $" [{d}]" : ""));
        }
    }
}
