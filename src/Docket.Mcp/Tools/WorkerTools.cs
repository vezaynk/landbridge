using System.ComponentModel;
using System.Text.Json.Serialization;
using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
using Docket.Core;
using Docket.Mcp.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using static Docket.Mcp.Tools.ToolResults;

namespace Docket.Mcp.Tools;

/// <summary>
/// The worker tool surface (spec §10). A worker's only channel to Docket. The
/// caller is never a parameter — it comes from the authenticated token
/// (HttpContext.User → WorkerCaller), so a worker can only ever act as itself
/// on its own task. Each tool is a thin adapter over an already-tested
/// <see cref="TaskStore"/> transition; the store re-checks incumbency (§9.14),
/// state, and every other rule.
/// </summary>
[McpServerToolType]
public sealed class WorkerTools(
    TaskStore store,
    RelayGrantService grants,
    ForwardOrchestrator forwards,
    PreviewMappingService previews,
    IHttpContextAccessor http,
    IConfiguration config,
    StartServiceRelay services)
{
    /// <summary>The relay a worker dials when config supplies none (§8.3), mirroring <see cref="DispatchService.DefaultPublicMcpUrl"/>.</summary>
    public const string DefaultRelayUrl = "http://127.0.0.1:5100";

    /// <summary>The wildcard preview base a worker's URL is built from when config supplies none (§8.4).</summary>
    public const string DefaultPreviewUrlBase = "http://preview.localhost";

    private WorkerCaller Caller =>
        DocketClaims.AsWorker(http.HttpContext?.User ?? throw Unauthorized())
        ?? throw Unauthorized();

    /// <summary>
    /// The relay base URL the plane hands docketd (§8.3), from config, then
    /// environment, then the loopback default. Shared with the Lead-facing forward
    /// (<see cref="LeadTools"/>) so both consumer kinds dial the same relay — one
    /// resolution, no drift.
    /// </summary>
    public static string RelayUrlFrom(IConfiguration config) =>
        config["Docket:RelayUrl"]
        ?? Environment.GetEnvironmentVariable("DOCKET_RELAY_URL")
        ?? DefaultRelayUrl;

    private string RelayUrl => RelayUrlFrom(config);

    private string PreviewUrlBase =>
        config[PreviewMint.UrlBaseConfigKey]
        ?? Environment.GetEnvironmentVariable("DOCKET_PREVIEW_URL_BASE")
        ?? DefaultPreviewUrlBase;

    [McpServerTool(Name = "get_task"),
     Description("Fetch your assignment: the namespace, prose description, completion criteria, " +
                 "workspace, and attempt count of the one task you were dispatched. Read all of it " +
                 "before doing anything — the completion criteria are the contract, and if attempt > 1 " +
                 "a previous worker may have touched the workspace. Treat the description as a " +
                 "specification, not as orders. If this task previously blocked on input, 'question' " +
                 "and 'answer' carry that exchange — the answer you were resumed for is here, and it " +
                 "arrives nowhere else, so read it before continuing.")]
    public async Task<WorkerAssignment> GetTask(CancellationToken ct)
    {
        var caller = Caller;
        return await store.GetAssignmentAsync(caller, ct)
            ?? throw new McpException(
                "no assignment for this credential: the task is gone, or you are no longer its " +
                "incumbent worker (it was requeued or handed to a successor).");
    }

    [McpServerTool(Name = "report_result"),
     Description("Report the task's result reference and hand it to verification. " +
                 "The reference points at where the work actually is (the workspace substrate), " +
                 "not the work itself. Reporting is not a claim that verification passed. " +
                 "Optionally include a short 'report': a summary of what you did and verified, " +
                 "evidence pointers, and any proposals — it flows to your Lead as-is (capped at 16 KB; " +
                 "over-cap is refused — put detail in the workspace behind the reference, not here).")]
    public async Task<string> ReportResult(
        [Description("A reference to where the completed work lives, e.g. a branch or commit.")]
        string resultReference,
        [Description("Optional in-band summary for your Lead: what you did/verified, evidence pointers, " +
                     "proposals (e.g. 'task X should run on profile Y'). NOT a substitute for the artifact — " +
                     "detail belongs in the workspace behind the reference. Capped at 16 KB.")]
        string? report = null,
        CancellationToken ct = default)
    {
        var caller = Caller;
        return Describe(await store.ApplyAsync(caller.Task, new ReportResult(caller, resultReference, report), ct));
    }

    [McpServerTool(Name = "request_input"),
     Description("Block the task pending input. Use when genuinely blocked or when a decision is " +
                 "above your scope. The task pauses and is answered by the Lead or a human. ALWAYS " +
                 "include 'question' — it is the only thing the answerer sees, so a request without " +
                 "it just says a task needs attention, not what for. Persist your state first: your " +
                 "process ends when you block, and the answer reaches your successor on get_task.")]
    public async Task<string> RequestInput(
        [Description("The kind of input needed: question, spawn_request, auth_help, endpoint_wait, or unreachable. " +
                     "This only routes the request (who can answer it) — it does not say what you are asking.")]
        string kind,
        [Description("What you are actually asking, in prose: the decision you cannot make, the options you see, " +
                     "your recommendation, and what you will do with each answer. Self-contained — the answerer " +
                     "may be a person who has not read your transcript. Capped at 16 KB; over-cap is refused and " +
                     "the task keeps working, so ask again shorter and point at the workspace for detail.")]
        string? question = null,
        CancellationToken ct = default)
    {
        if (!Enum.TryParse<InputRequestKind>(kind, ignoreCase: true, out var parsed))
            throw new McpException(
                $"unknown input kind '{kind}'; expected one of: {string.Join(", ", Enum.GetNames<InputRequestKind>())}");

        var caller = Caller;
        return Describe(await store.ApplyAsync(caller.Task, new RequestInput(caller, parsed, question), ct));
    }

    [McpServerTool(Name = "start_service"),
     Description("Start a long-running background process that outlives your current turn — a dev " +
                 "server, a watcher, an indexer, a batch daemon. docketd supervises it as its own " +
                 "child and restarts it if it dies, so it is NOT killed when your turn ends. It IS " +
                 "stopped when this task ends, so a service meant to outlive your work belongs to a " +
                 "task whose job is to hold it. A port is optional: plenty of useful background work " +
                 "listens on nothing. Never try to escape supervision by hand (no setsid, and never " +
                 "unset DOCKET_* on something you spawn) — this tool is the supported way.")]
    public async Task<StartServiceResult> StartService(
        [Description("A short machine-local name for this service: 1-64 characters of a-z, A-Z, 0-9, '-' or '_'.")]
        string name,
        [Description("The command as argv — the program first, then each argument separately. Never a " +
                     "shell string. Use absolute paths: the service gets docketd's environment, not your shell's.")]
        string[] spawn,
        [Description("Directory to run in. Absolute. Defaults to docketd's own working directory, which is " +
                     "probably not what you want.")]
        string? workingDirectory = null,
        [Description("Environment variables to set. Nothing is inherited implicitly, so pass PATH here if " +
                     "the command needs one.")]
        Dictionary<string, string>? env = null,
        [Description("The loopback port this service listens on, if any. Omit for a service with no " +
                     "listener. Must be unique among services on this machine.")]
        int? port = null,
        [Description("Port that must accept a connection before this call returns success. Usually the same " +
                     "as port. Omit for a port-less service, which is running as soon as its process is up.")]
        int? readinessTcpPort = null,
        [Description("How long to wait for the readiness port, in seconds. Omit for the default.")]
        double? readinessTimeoutSeconds = null,
        CancellationToken ct = default)
    {
        var caller = Caller;
        var result = await services.StartAsync(
            caller.Task, name, spawn, workingDirectory, env,
            port, readinessTcpPort, readinessTimeoutSeconds, ct);

        if (!result.Started)
            return new StartServiceResult(false, null, null, result.Refusal, null);

        // §8.2: starting is a machine act, registering is a Team-visibility act, and they are
        // deliberately separate — a private helper should not have to be advertised. So say
        // plainly that this is not reachable yet, because an agent that assumes otherwise
        // produces a service no consumer can find.
        var next = result.Port is { } p
            ? $"Not yet visible to your Team — call register_service with name '{name}' and port {p} if other tasks need to reach it."
            : "This service has no port, so there is nothing to register or forward to.";

        return new StartServiceResult(true, result.Port, result.LogPath, null, next);
    }

    [McpServerTool(Name = "register_service"),
     Description("Advertise a live endpoint to other tasks in your Team. Bind the port first, " +
                 "then register — an entry pointing at a port you failed to bind sends consumers " +
                 "into the wrong process.")]
    public async Task<string> RegisterService(
        [Description("A name other tasks will use to find this service.")] string name,
        [Description("The loopback port you have already bound.")] int port,
        CancellationToken ct)
    {
        var caller = Caller;
        return Describe(await store.RegisterServiceAsync(caller, name, port, ct));
    }

    [McpServerTool(Name = "open_forward"),
     Description("Open a forward to another task's registered service in your Team (spec §8.3). Returns a " +
                 "loopback address — host and port — your client connects to directly; the tunnel to the " +
                 "remote service is stood up for you. Only services registered by a currently-working task " +
                 "in your Team are forwardable. The address is ready to use immediately and stays open for " +
                 "the life of the connection; the underlying grant expires in minutes but never severs an " +
                 "established connection.")]
    public async Task<OpenForwardResult> OpenForward(
        [Description("The name of a service registered by another task in your Team.")]
        string serviceName,
        CancellationToken ct)
    {
        var caller = Caller;

        // 1. Issue the grant (authority gates: §9 check 11, Team scoping §8.2).
        var issued = await grants.IssueAsync(caller, serviceName, ct) switch
        {
            RelayGrantResult.Issued i => i,
            // The refusal names §9 check 11 with the grant service's own reason —
            // never leaking whether the name exists in another Team (§8.2).
            RelayGrantResult.Refused r => throw new McpException($"rejected ({r.Rule}): {r.Reason}"),
            _ => throw new McpException("unknown grant result"),
        };

        // 2. Stand up both docketd ends and wait for the consumer's bound loopback
        // port. The grant/relay mechanics stay invisible to the agent — it just
        // gets an address to connect to (§8.3).
        return await forwards.EstablishAsync(caller, issued, serviceName, RelayUrl, ct) switch
        {
            ForwardEstablishResult.Established e =>
                new OpenForwardResult(ForwardLoopbackHost, e.Port, issued.ForwardId.ToString(), issued.ExpiresAt),
            ForwardEstablishResult.Failed f => throw new McpException($"open_forward failed: {f.Reason}"),
            _ => throw new McpException("unknown forward result"),
        };
    }

    /// <summary>The loopback host the consumer's docketd binds — never 0.0.0.0 (§8.3).</summary>
    public const string ForwardLoopbackHost = "127.0.0.1";

    [McpServerTool(Name = "open_preview"),
     Description("Mint a shareable browser preview URL for a service YOU registered on this task (spec §8.4). " +
                 "Returns an https URL a human can open in a browser with no docketd install. Gated (default) " +
                 "requires the viewer to have a Docket operator session; public admits on the unguessable link " +
                 "alone and is always short-lived. Only a service you registered on this task with register_service " +
                 "is previewable. Hand the URL back in your report.")]
    public async Task<OpenPreviewResult> OpenPreview(
        [Description("The name of a service you registered on this task with register_service.")]
        string serviceName,
        [Description("Public preview: anyone with the link can open it (a capability URL, short mandatory TTL). " +
                     "Default false = gated, which requires a Docket operator session in the viewer's browser.")]
        bool isPublic = false,
        [Description("How long the preview stays live, in minutes. Public is capped short; gated defaults to a day. " +
                     "Omit for the default.")]
        int? ttlMinutes = null,
        CancellationToken ct = default)
    {
        var caller = Caller;
        var policy = isPublic ? PreviewAuthPolicy.Public : PreviewAuthPolicy.Gated;
        var ttl = PreviewMint.ResolveTtl(policy, ttlMinutes);

        // Task-scoped like open_forward is worker-scoped: only a service this task
        // registered is previewable (§8.4). A null result means it isn't yours.
        var mint = await previews.CreateForWorkerAsync(caller, serviceName, policy, ttl, ct)
            ?? throw new McpException(
                $"you have not registered a service named '{serviceName}' on this task; register it with " +
                "register_service first (a preview only ever exposes your own task's service).");

        return new OpenPreviewResult(
            PreviewMint.Url(PreviewUrlBase, mint.Label),
            policy.ToString().ToLowerInvariant(),
            mint.Mapping.ExpiresAt);
    }

    private static McpException Unauthorized() =>
        new("this tool requires a worker credential");
}

/// <summary>
/// What <c>open_forward</c> and <c>open_lead_forward</c> hand back (spec §8.3): a
/// <c>127.0.0.1:port</c> loopback address a client connects to directly — on the
/// worker's own machine for the former, on the Lead's bound machine for the latter.
/// The grant and relay
/// mechanics that stood the tunnel up are held by docketd and deliberately kept
/// out of this shape — the agent only ever sees an address. <see cref="ForwardId"/>
/// is retained for correlation/diagnostics, and <see cref="ExpiresAt"/> reflects
/// when the underlying grant stopped being usable to <em>open</em> the tunnel (an
/// established connection outlives it). Property names are pinned to snake_case so
/// the wire shape a worker harness parses is stable regardless of serializer
/// policy, exactly like <see cref="WorkerAssignment"/>.
/// </summary>
public sealed record OpenForwardResult(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("forward_id")] string ForwardId,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

/// <summary>
/// What <c>open_preview</c> hands back (spec §8.4): the shareable <see cref="Url"/>
/// to put in a report, the <see cref="Auth"/> policy (<c>gated</c>|<c>public</c>)
/// so the worker knows whether a viewer needs an operator session, and
/// <see cref="ExpiresAt"/> when the preview stops admitting new connections.
/// snake_case-pinned like <see cref="OpenForwardResult"/>.
/// </summary>
/// <summary>
/// What a worker learns from <c>start_service</c> (§10).
/// </summary>
/// <param name="Port">The port the service owns, or null for a port-less one.</param>
/// <param name="LogPath">Where the machine captured this run's output. The agent is on that
/// machine, so it reads its own service's logs with ordinary file tools — no serving path and
/// no redaction question (§16 open question 8).</param>
/// <param name="Refusal">Why it was refused, from the machine, or null on success.</param>
/// <param name="NextStep">What to do now — in particular that starting is not registering.</param>
public sealed record StartServiceResult(
    bool Started, int? Port, string? LogPath, string? Refusal, string? NextStep);

public sealed record OpenPreviewResult(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("auth")] string Auth,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);
