using System.Text.Json;
using System.Text.Json.Serialization;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Landbridge.Web;
using Microsoft.Extensions.Configuration;

namespace Landbridge.Mcp.Dashboard;

/// <summary>
/// Operator-only stand-in for the unbuilt §11 conformance run: mint dummy sessions
/// aimed at one profile, then poll their states. POST takes a required
/// <c>profile</c> (JSON body or form field). Omit is 400. The plane still
/// does not judge the work — a session that is <c>awaiting_report</c> is a
/// worker that called <c>report_result</c>. Human-only, like the Machine
/// Group: a Lead cannot enumerate profiles and must not mint fleet-wide work.
/// </summary>
internal static class ConformanceEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
    };

    public static IEndpointRouteBuilder MapConformance(this IEndpointRouteBuilder app)
    {
        app.MapPost("/dashboard/conformance", HandleStartAsync).DisableAntiforgery().WithOrder(-100);
        return app;
    }

    /// <summary>
    /// POST /dashboard/conformance — create the dummy set for a required
    /// <c>profile</c> (JSON or form). Same-origin, human-only. The run id is a
    /// new Team id; progress is <c>GET /dashboard/conformance/{runId}</c>.
    /// </summary>
    private static async Task<IResult> HandleStartAsync(
        HttpContext http, TokenService tokens, SessionStore store,
        RunnerConnectionRegistry registry, IConfiguration config, CancellationToken ct)
    {
        if (CrossOriginRefusal(http, config) is { } refusal)
            return refusal;

        return await GatedHuman(http, tokens, ct, async _ =>
        {
            var profile = await ReadProfileAsync(http, ct);
            if (profile is null)
                return Results.Json(new { error = "profile is required" }, Json,
                    statusCode: StatusCodes.Status400BadRequest);

            var runId = TeamId.New();
            var lead = new LeadClaim(runId);
            var specs = ConformanceCatalog.For(runId.Value);
            var created = new List<ConformanceSessionView>(specs.Count);
            foreach (var spec in specs)
            {
                var result = await store.CreateAsync(
                    new CreateSession(
                        lead, runId, spec.Description, profile,
                        Workspace: ConformanceCatalog.WorkspaceOf(spec.Kind)),
                    ct);
                if (result is not StoreResult.Applied applied)
                    return Results.Json(new { error = "failed to create a dummy session", detail = result.ToString() },
                        Json, statusCode: StatusCodes.Status500InternalServerError);
                created.Add(new ConformanceSessionView(
                    applied.Session.Id.Value, spec.Kind, applied.Session.State, applied.Session.Attempt,
                    ResultReference: null, LastRequeueReason: null));
            }

            var machines = MachinesDeclaring(registry, profile);
            var model = ConformanceRunView.From(runId.Value, profile, created, machines);
            return DashboardNegotiate.WantsJson(http) || http.Request.HasJsonContentType()
                ? Results.Json(model, Json, statusCode: StatusCodes.Status201Created)
                : Results.Redirect($"/dashboard/conformance/{runId.Value:D}");
        });
    }

    /// <summary>
    /// JSON body <c>{ "profile": "goose-devbox-linux" }</c> or form field
    /// <c>profile</c>. Required. Exact string — the same match dispatch uses.
    /// A name no machine declares is accepted and sits in Submitted.
    /// </summary>
    private static async Task<string?> ReadProfileAsync(HttpContext http, CancellationToken ct)
    {
        string? raw = null;
        if (http.Request.HasJsonContentType())
        {
            var body = await http.Request.ReadFromJsonAsync<ConformanceStartBody>(Json, ct);
            raw = body?.Profile;
        }
        else if (http.Request.HasFormContentType)
        {
            var form = await http.Request.ReadFormAsync(ct);
            raw = form["profile"].ToString();
        }

        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private static IReadOnlyList<string> MachinesDeclaring(RunnerConnectionRegistry registry, string profile)
    {
        var found = new List<string>();
        foreach (var id in registry.MachineIds())
        {
            var snap = registry.SnapshotFor(id);
            if (snap?.DeclaredProfiles.Contains(profile) == true)
                found.Add(id);
        }
        return found;
    }

    private static async Task<IResult> GatedHuman(
        HttpContext http, TokenService tokens, CancellationToken ct,
        Func<Principal.Human, Task<IResult>> body)
    {
        if (await DashboardAuth.ResolveAsync(http, tokens, ct) is not { } principal)
            return DashboardNegotiate.WantsJson(http)
                ? Results.Json(new { error = "unauthorized" }, Json, statusCode: 401)
                : Results.Redirect("/dashboard/login");
        if (principal is not Principal.Human human)
            return DashboardNegotiate.WantsJson(http)
                ? Results.Json(new { error = MachinesAreHumanOnly }, Json, statusCode: StatusCodes.Status403Forbidden)
                : DashboardHosting.RazorPage<Components.Pages.ScopeRefusedPage>(
                    new { Reason = MachinesAreHumanOnly }, StatusCodes.Status403Forbidden);
        return await body(human);
    }

    private static IResult? CrossOriginRefusal(HttpContext http, IConfiguration config)
    {
        var origin = config["Landbridge:PublicMcpUrl"]
            ?? Environment.GetEnvironmentVariable("LANDBRIDGE_PUBLIC_MCP_URL");
        return OriginGuard.IsSameOrigin(http.Request, origin)
            ? null
            : DashboardNegotiate.WantsJson(http)
                ? Results.Json(new { error = CrossOriginReason }, Json, statusCode: StatusCodes.Status403Forbidden)
                : DashboardHosting.RazorPage<Components.Pages.ScopeRefusedPage>(
                    new { Reason = CrossOriginReason }, StatusCodes.Status403Forbidden);
    }

    private const string MachinesAreHumanOnly =
        "profile checks are a human-operator view; a Lead session sees its own Team's tasks "
        + "on /dashboard/teams and through get_team_state";

    private const string CrossOriginReason =
        "this form is same-origin only: the request carried no Origin from the dashboard's own host";
}

internal sealed record ConformanceStartBody(string? Profile);

internal sealed record ConformanceSessionView(
    Guid SessionId,
    string Kind,
    SessionState State,
    int Attempt,
    string? ResultReference,
    string? LastRequeueReason,
    MessageState MessageState = MessageState.Idle);

internal sealed record ConformanceRunView(
    Guid RunId,
    string Profile,
    string ProgressUrl,
    int Total,
    int Pending,
    int Reported,
    int Completed,
    int Failed,
    bool WorkerDone,
    IReadOnlyList<string> MachinesDeclaring,
    IReadOnlyList<ConformanceSessionView> Sessions)
{
    public static ConformanceRunView From(
        Guid runId, string profile, IReadOnlyList<ConformanceSessionView> tasks,
        IReadOnlyList<string> machines)
    {
        var pending = 0;
        var reported = 0;
        var completed = 0;
        var failed = 0;
        foreach (var t in tasks)
        {
            switch (ConformanceCatalog.Bucket(t.State, t.MessageState))
            {
                case "reported": reported++; break;
                case "completed": completed++; break;
                case "failed": failed++; break;
                default: pending++; break;
            }
        }

        return new(
            runId, profile, $"/dashboard/conformance/{runId:D}",
            tasks.Count, pending, reported, completed, failed,
            pending == 0 && failed == 0 && tasks.Count > 0,
            machines, tasks);
    }
}
