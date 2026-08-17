using Docket.ControlPlane.Auth;
using Docket.Core;
using Microsoft.Extensions.Logging;

namespace Docket.ControlPlane;

/// <summary>
/// The control-plane half of one HTTP-preview browser connection (spec §8.4).
/// The preview frontend calls this once per browser connection (via the
/// <c>/preview/connect</c> endpoint); it resolves the subdomain label, enforces
/// the mapping's auth-policy and check 11, mints a fresh consumer grant + forward
/// id, and relays the producer <c>docketd</c> its dial target so it dials on
/// demand. On success the frontend receives <c>{grant, forwardId, relayUrl}</c>
/// and dials the relay's unchanged <c>/tunnel</c> as the consumer itself.
///
/// <para>Every failure is a typed result carrying a clean outcome — never a throw,
/// never a hang — so the endpoint renders a definite HTTP status (§8.4: failure
/// modes return statuses, not hangs). It composes the already-tested
/// <see cref="PreviewMappingService"/>, <see cref="RelayGrantService"/>,
/// <see cref="TokenService"/>, and <see cref="ForwardOrchestrator"/>, so the logic
/// is unit-testable against Postgres without hosting the web endpoint.</para>
/// </summary>
public sealed class PreviewConnectService(
    PreviewMappingService mappings,
    RelayGrantService grants,
    TokenService tokens,
    PreviewAuthStore previewAuth,
    ForwardOrchestrator forwards,
    ILogger<PreviewConnectService> logger)
{
    /// <summary>
    /// Authorize and orchestrate one browser connection for <paramref name="label"/>.
    /// For a gated mapping, admission comes from EITHER a per-label preview session
    /// (<paramref name="previewSession"/> — the browser's <c>docket_preview</c>
    /// cookie, minted through the §8.4 redirect flow) OR a §12 operator session
    /// (<paramref name="operatorSession"/> — a bearer, the tooling path). Public
    /// admits on the label alone. <paramref name="relayUrl"/> is the relay base URL
    /// the frontend will dial and the producer is told to dial — the plane owns it
    /// (config), so both ends agree without the frontend guessing.
    /// </summary>
    public async Task<PreviewConnectResult> ConnectAsync(
        string label, string? operatorSession, string? previewSession, string relayUrl,
        CancellationToken ct = default)
    {
        // 1. Resolve the label → mapping (§8.4). Unknown/expired are clean refusals
        // the endpoint renders as distinct statuses (404/410).
        PreviewMappingRow mapping;
        switch (await mappings.ResolveAsync(label, ct))
        {
            case PreviewResolveResult.Found found:
                mapping = found.Mapping;
                break;
            case PreviewResolveResult.Expired:
                return PreviewConnectResult.Expired.Instance;
            default:
                return PreviewConnectResult.NotFound.Instance;
        }

        // 2. Enforce the mapping's auth-policy (§8.4). Gated (default) requires
        // either a per-label preview session (the browser's docket_preview cookie
        // from the redirect flow) or a §12 operator session (a Human, or a Lead on
        // this mapping's Team — the bearer/tooling path). Public admits on the
        // unguessable label alone (already time-boxed by the mapping TTL at resolve).
        if (mapping.AuthPolicy == PreviewAuthPolicy.Gated
            && !previewAuth.ValidateSession(previewSession, label)
            && !await IsOperatorAuthorizedAsync(operatorSession, new TeamId(mapping.TeamId), ct))
            return PreviewConnectResult.Unauthorized.Instance;

        // 3. Check 11 + mint a fresh consumer grant + forward id for this one
        // connection (§8.4). The grant service re-verifies the service is still
        // registered and its owning task still working — a mint recorded a task
        // that may since have left working.
        //
        // Resolved by (task, name) from the mapping, never by name alone: the label was
        // minted against ONE task's service and the mapping records which
        // (PreviewMappingRow.SessionId). Passing only the name made that column write-only and
        // handed the URL whatever answered to the name at connect time — so a label minted
        // for task A's `web` could splice to task B's `web` once A finished, which is a
        // preview reaching a service its holder never exposed. The task is the authority
        // here; the name alone is not.
        if (await grants.IssueForPreviewAsync(
                new TeamId(mapping.TeamId), new SessionId(mapping.SessionId), mapping.ServiceName, ct)
            is not RelayGrantResult.Issued issued)
        {
            logger.LogInformation(
                "preview connect: check 11 refused for team {Team} task {Task} service {Service}",
                mapping.TeamId, mapping.SessionId, mapping.ServiceName);
            return new PreviewConnectResult.Unavailable(
                $"service '{mapping.ServiceName}' is not currently reachable");
        }

        // 4. Relay the producer docketd its dial target so it dials on demand
        // (§8.4). No consumer command and no forward-opened wait — the frontend is
        // the consumer. A gone/unreachable producer machine is a clean refusal.
        var forwardId = issued.ForwardId.ToString();
        if (!await forwards.SendProducerDialAsync(
                issued.Producer, forwardId, mapping.ServiceName, issued.Grant, relayUrl, issued.Port, ct))
            return new PreviewConnectResult.Unavailable(
                $"the machine hosting service '{mapping.ServiceName}' is not connected");

        logger.LogInformation(
            "preview connect: label resolved to team {Team} service {Service}, forward {ForwardId} armed",
            mapping.TeamId, mapping.ServiceName, forwardId);
        return new PreviewConnectResult.Established(issued.Grant, forwardId, relayUrl);
    }

    private async Task<bool> IsOperatorAuthorizedAsync(string? session, TeamId team, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(session))
            return false;
        // Same validation the dashboard uses (§12): a live Human session sees any
        // preview; a Lead is scoped to its own Team. An evicted, expired, or worker
        // token authorizes nothing here.
        return await tokens.ValidateAsync(session, ct) switch
        {
            Principal.Human => true,
            Principal.Lead l => l.Team == team,
            _ => false,
        };
    }
}

/// <summary>
/// Outcome of <see cref="PreviewConnectService.ConnectAsync"/> (§8.4). The
/// <c>/preview/connect</c> endpoint maps each onto a definite HTTP status:
/// <see cref="Established"/> → 200, <see cref="NotFound"/> → 404,
/// <see cref="Expired"/> → 410, <see cref="Unauthorized"/> → 401,
/// <see cref="Unavailable"/> → 503.
/// </summary>
public abstract record PreviewConnectResult
{
    private PreviewConnectResult() { }

    /// <summary>The producer was armed; the frontend dials the relay as consumer with these.</summary>
    public sealed record Established(string Grant, string ForwardId, string RelayUrl) : PreviewConnectResult;

    /// <summary>No mapping for the label.</summary>
    public sealed record NotFound : PreviewConnectResult
    {
        public static readonly NotFound Instance = new();
    }

    /// <summary>The mapping is past its TTL.</summary>
    public sealed record Expired : PreviewConnectResult
    {
        public static readonly Expired Instance = new();
    }

    /// <summary>Gated preview, and the request carried no valid operator session.</summary>
    public sealed record Unauthorized : PreviewConnectResult
    {
        public static readonly Unauthorized Instance = new();
    }

    /// <summary>Check 11 failed, or the producer machine is not connected. <see cref="Reason"/> is caller-facing.</summary>
    public sealed record Unavailable(string Reason) : PreviewConnectResult;
}
