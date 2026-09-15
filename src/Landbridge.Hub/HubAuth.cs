using System.Security.Claims;
using System.Text.Encodings.Web;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Landbridge.Hub;

/// <summary>
/// Bearer-only auth for the hub. Same <see cref="TokenService"/> lookup as
/// Core; no cookies, no OAuth challenge. The typed principal rides
/// <see cref="HttpContext.Items"/> for the rest of the request.
/// </summary>
public sealed class HubAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TokenService tokens)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "LandbridgeHub";
    public const string PrincipalKey = "landbridge.hub.principal";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header))
            return AuthenticateResult.NoResult();

        var value = header.ToString();
        const string prefix = "Bearer ";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var principal = await tokens.ValidateAsync(value[prefix.Length..].Trim(), Context.RequestAborted);
        if (principal is null)
            return AuthenticateResult.Fail("invalid or revoked token");

        Context.Items[PrincipalKey] = principal;
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, SchemeName)], SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(user, SchemeName));
    }
}

/// <summary>
/// Read authorization for one Hub request. Human is instance-wide. Lead is
/// owned Teams. Worker is that session. Machine tokens are refused.
/// </summary>
public sealed class HubCaller
{
    public required Principal Principal { get; init; }
    public IReadOnlySet<Guid>? Teams { get; init; }
    public Guid? WorkerSession { get; init; }
    public Guid? WorkerTeam { get; init; }
    public IResult? Error { get; init; }

    public static async Task<HubCaller> ResolveAsync(
        HttpContext http, TokenService tokens, CancellationToken ct)
    {
        if (http.Items[HubAuthenticationHandler.PrincipalKey] is not Principal principal)
            return Deny(StatusCodes.Status401Unauthorized, "unauthorized");

        switch (principal)
        {
            case Principal.Machine:
                return Deny(StatusCodes.Status403Forbidden, "machine tokens cannot read the hub");
            case Principal.EvictedLead e:
                return Deny(StatusCodes.Status403Forbidden,
                    $"your lead claim on team {e.Team.Value:N} was taken over by human " +
                    $"{e.EvictedByHuman:N} at {e.EvictedAt:O}; reattach to the Team to continue.");
            case Principal.Human:
                return new HubCaller { Principal = principal, Teams = null };
            case Principal.Lead lead:
                var owned = await tokens.OwnedTeamIdsAsync(lead.CredentialId, ct);
                return new HubCaller
                {
                    Principal = principal,
                    Teams = owned.ToHashSet(),
                };
            case Principal.Worker w:
                return new HubCaller
                {
                    Principal = principal,
                    Teams = new HashSet<Guid> { w.Caller.Team.Value },
                    WorkerSession = w.Caller.Session.Value,
                    WorkerTeam = w.Caller.Team.Value,
                };
            default:
                return Deny(StatusCodes.Status403Forbidden, "forbidden");
        }
    }

    public bool MayTeam(Guid teamId) => Principal switch
    {
        Principal.Human => true,
        Principal.Lead => Teams is not null && Teams.Contains(teamId),
        Principal.Worker => WorkerTeam == teamId,
        _ => false,
    };

    public bool MaySession(Guid sessionId, Guid teamId) => Principal is Principal.Worker
        ? WorkerSession == sessionId
        : MayTeam(teamId);

    public bool MayMachines => Principal is Principal.Human or Principal.Lead;

    public bool MayProcesses => Principal is Principal.Human;

    public bool MayWatch(string topic, Guid? entityId)
    {
        if (Error is not null)
            return false;
        if (Principal is Principal.Human)
            return true;
        if (Principal is Principal.Lead)
            return topic is not (HubQueueRow.ProcessesTopic or HubQueueRow.ProcessTopic);
        if (Principal is Principal.Worker)
        {
            if (entityId is null || entityId != WorkerSession)
                return false;
            return topic is HubQueueRow.SessionTopic or HubQueueRow.EventsTopic
                or HubQueueRow.ExchangeTopic or HubQueueRow.ServicesTopic;
        }

        return false;
    }

    public static IResult Forbid(string message = "forbidden") =>
        Deny(StatusCodes.Status403Forbidden, message).Error!;

    private static HubCaller Deny(int status, string message) => new()
    {
        Principal = new Principal.Machine(Guid.Empty),
        Error = TypedResults.Json(new { error = message }, HubEndpoints.Json, statusCode: status),
    };
}

public static class HubAuthExtensions
{
    public static IServiceCollection AddHubAuth(this IServiceCollection services)
    {
        services.AddScoped<TokenService>();
        services.AddAuthentication(HubAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HubAuthenticationHandler>(
                HubAuthenticationHandler.SchemeName, configureOptions: null);
        services.AddAuthorization();
        return services;
    }
}
