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
/// aimed at one profile, then poll their states. POST takes <c>profile</c> (JSON
/// body or form field); omit is <c>default</c>. The plane still does not judge
/// the work — a session that reaches <c>verifying</c> is a worker that called
/// <c>report_result</c>. Human-only, like the Machine Group: a Lead cannot
/// enumerate profiles and must not mint fleet-wide work.
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
        app.MapGet("/dashboard/conformance", HandleFormAsync);
        app.MapPost("/dashboard/conformance", HandleStartAsync);
        app.MapGet("/dashboard/conformance/{runId}", HandleProgressAsync);
        return app;
    }

    /// <summary>GET /dashboard/conformance — the start form (HTML) or the schema (JSON).</summary>
    private static async Task<IResult> HandleFormAsync(
        HttpContext http, TokenService tokens, CancellationToken ct)
    {
        return await GatedHuman(http, tokens, ct, _ =>
        {
            if (WantsJson(http))
            {
                return Task.FromResult<IResult>(Results.Json(new
                {
                    post = "/dashboard/conformance",
                    profile = MachineSnapshot.DefaultProfile,
                    profileField = "optional; omit or empty is default; exact-match name from the runner config",
                    kinds = ConformanceCatalog.Kinds,
                }, Json));
            }

            return Task.FromResult<IResult>(Html(DashboardRenderer.ConformanceForm()));
        });
    }

    /// <summary>
    /// POST /dashboard/conformance — create the dummy set for <c>profile</c>
    /// (JSON or form; omit is <c>default</c>). Same-origin, human-only. The run
    /// id is a new Team id; progress is <c>GET /dashboard/conformance/{runId}</c>.
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

            var runId = TeamId.New();
            var lead = new LeadClaim(runId);
            var specs = ConformanceCatalog.For(runId.Value);
            var created = new List<ConformanceSessionView>(specs.Count);
            foreach (var spec in specs)
            {
                var result = await store.CreateAsync(
                    new CreateSession(
                        lead, runId, spec.CompletionCriteria, CompletionMode.Lead, profile,
                        Description: spec.Description,
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
            return WantsJson(http) || http.Request.HasJsonContentType()
                ? Results.Json(model, Json, statusCode: StatusCodes.Status201Created)
                : Results.Redirect($"/dashboard/conformance/{runId.Value:D}");
        });
    }

    /// <summary>GET /dashboard/conformance/{runId} — current states of that run's tasks.</summary>
    private static async Task<IResult> HandleProgressAsync(
        string runId, HttpContext http, TokenService tokens, DashboardQueries queries,
        RunnerConnectionRegistry registry, CancellationToken ct)
    {
        if (!Guid.TryParse(runId, out var id))
            return Results.BadRequest(new { error = "invalid run id" });

        return await GatedHuman(http, tokens, ct, async _ =>
        {
            var rows = await queries.GetConformanceTasksAsync(id, ct);
            if (rows.Count == 0)
            {
                return WantsJson(http)
                    ? Results.Json(new { error = "no such conformance run" }, Json, statusCode: 404)
                    : Results.Content(
                        DashboardRenderer.ConformanceMissing(), "text/html; charset=utf-8",
                        statusCode: StatusCodes.Status404NotFound);
            }

            var profile = rows[0].Profile ?? "";
            var tasks = rows.Select(r => new ConformanceSessionView(
                r.Id,
                ConformanceCatalog.KindOf(r.Workspace) ?? "unknown",
                r.State, r.Attempt, r.ResultReference, r.LastRequeueReason?.ToString())).ToList();
            var model = ConformanceRunView.From(id, profile, tasks, MachinesDeclaring(registry, profile));
            return WantsJson(http)
                ? Results.Json(model, Json)
                : Html(DashboardRenderer.ConformanceRun(model));
        });
    }

    /// <summary>
    /// JSON body <c>{ "profile": "goose" }</c> or form field <c>profile</c>.
    /// Omit, empty, or whitespace is <see cref="MachineSnapshot.DefaultProfile"/>.
    /// Exact string — the same match dispatch uses. A name no machine declares
    /// is accepted and sits in Submitted, which is the quiet failure enroll hunts.
    /// </summary>
    private static async Task<string> ReadProfileAsync(HttpContext http, CancellationToken ct)
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

        return string.IsNullOrWhiteSpace(raw) ? MachineSnapshot.DefaultProfile : raw.Trim();
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
            return WantsJson(http)
                ? Results.Json(new { error = "unauthorized" }, Json, statusCode: 401)
                : Results.Redirect("/dashboard/login");
        if (principal is not Principal.Human human)
            return WantsJson(http)
                ? Results.Json(new { error = MachinesAreHumanOnly }, Json, statusCode: StatusCodes.Status403Forbidden)
                : Results.Content(
                    DashboardRenderer.ScopeRefused(MachinesAreHumanOnly), "text/html; charset=utf-8",
                    statusCode: StatusCodes.Status403Forbidden);
        return await body(human);
    }

    private static IResult? CrossOriginRefusal(HttpContext http, IConfiguration config)
    {
        var origin = config["Landbridge:PublicMcpUrl"]
            ?? Environment.GetEnvironmentVariable("LANDBRIDGE_PUBLIC_MCP_URL");
        return OriginGuard.IsSameOrigin(http.Request, origin)
            ? null
            : WantsJson(http)
                ? Results.Json(new { error = CrossOriginReason }, Json, statusCode: StatusCodes.Status403Forbidden)
                : Results.Content(
                    DashboardRenderer.ScopeRefused(CrossOriginReason), "text/html; charset=utf-8",
                    statusCode: StatusCodes.Status403Forbidden);
    }

    private static IResult Html(string html) =>
        Results.Content(html, "text/html; charset=utf-8");

    private static bool WantsJson(HttpContext http)
    {
        var format = http.Request.Query["format"].ToString();
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            return true;
        var accept = http.Request.Headers.Accept.ToString();
        return accept.Contains("application/json", StringComparison.OrdinalIgnoreCase)
            && !accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
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
    string? LastRequeueReason);

internal sealed record ConformanceRunView(
    Guid RunId,
    string Profile,
    string ProgressUrl,
    int Total,
    int Pending,
    int Verifying,
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
        var verifying = 0;
        var completed = 0;
        var failed = 0;
        foreach (var t in tasks)
        {
            switch (ConformanceCatalog.Bucket(t.State))
            {
                case "verifying": verifying++; break;
                case "completed": completed++; break;
                case "failed": failed++; break;
                default: pending++; break;
            }
        }

        return new(
            runId, profile, $"/dashboard/conformance/{runId:D}",
            tasks.Count, pending, verifying, completed, failed,
            pending == 0 && failed == 0 && tasks.Count > 0,
            machines, tasks);
    }
}
