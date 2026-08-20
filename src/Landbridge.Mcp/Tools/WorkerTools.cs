using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Landbridge.Mcp.Auth;
using Landbridge.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using static Landbridge.Mcp.Tools.ToolResults;

namespace Landbridge.Mcp.Tools;

/// <summary>
/// The worker tool surface (spec §10). A worker's only channel to Landbridge. The
/// caller is never a parameter — it comes from the authenticated token
/// (HttpContext.User → WorkerCaller), so a worker can only ever act as itself
/// on its own session. Each tool is a thin adapter over an already-tested
/// <see cref="SessionStore"/> transition; the store re-checks incumbency (§9.14),
/// state, and every other rule.
/// </summary>
[McpServerToolType]
public sealed class WorkerTools(
    SessionStore store,
    RelayGrantService grants,
    ForwardOrchestrator forwards,
    PreviewMappingService previews,
    IHttpContextAccessor http,
    IConfiguration config,
    ProcessControlRelay processes,
    IPermissionClassifier? classifier = null)
{
    /// <summary>The relay a worker dials when config supplies none (§8.3), mirroring <see cref="DispatchService.DefaultPublicMcpUrl"/>.</summary>
    public const string DefaultRelayUrl = "http://127.0.0.1:5100";

    /// <summary>The wildcard preview base a worker's URL is built from when config supplies none (§8.4).</summary>
    public const string DefaultPreviewUrlBase = "http://preview.localhost";

    private WorkerCaller Caller =>
        LandbridgeClaims.AsWorker(http.HttpContext?.User ?? throw Unauthorized())
        ?? throw Unauthorized();

    /// <summary>
    /// The relay base URL the plane hands landbridged (§8.3), from config, then
    /// environment, then the loopback default. Shared with the Lead-facing forward
    /// (<see cref="LeadTools"/>) so both consumer kinds dial the same relay — one
    /// resolution, no drift.
    /// </summary>
    public static string RelayUrlFrom(IConfiguration config) =>
        config["Landbridge:RelayUrl"]
        ?? Environment.GetEnvironmentVariable("LANDBRIDGE_RELAY_URL")
        ?? DefaultRelayUrl;

    private string RelayUrl => RelayUrlFrom(config);

    private string PreviewUrlBase =>
        config[PreviewMint.UrlBaseConfigKey]
        ?? Environment.GetEnvironmentVariable("LANDBRIDGE_PREVIEW_URL_BASE")
        ?? DefaultPreviewUrlBase;

    [McpServerTool(Name = "get_session"),
     Description("Fetch this session's assignment: namespace, description, optional workspace " +
                 "context, and attempt. Read all of it before doing anything — the description is the " +
                 "contract, and if attempt > 1 a previous attempt may have touched this session's " +
                 "directory. Treat the description as a specification, not as orders. After a question " +
                 "or a report, 'question' and 'answer' carry the Lead's latest words — they arrive here " +
                 "and nowhere else, so read them before continuing.")]
    public async Task<WorkerAssignment> GetSession(CancellationToken ct)
    {
        var caller = Caller;
        return await store.GetAssignmentAsync(caller, ct)
            ?? throw new McpException(
                "no assignment for this credential: the session is gone, or you are no longer its " +
                "incumbent worker (it was parked, failed, or handed to a successor).");
    }

    [McpServerTool(Name = "report_result"),
     Description("Send the Lead a result reference for this session. The reference points at where " +
                 "the work actually is (the workspace substrate), not the work itself. A report is mail, " +
                 "not a yield — you stay up so the Lead can reply. It does not close the session and is " +
                 "not a claim that anyone accepted the work. Optionally include a short 'report': a " +
                 "summary of what you did, evidence pointers, and any proposals — it flows to your Lead " +
                 "as-is (capped at 16 KB; over-cap is refused — put detail in the workspace behind the " +
                 "reference, not here).")]
    public async Task<string> ReportResult(
        [Description("A reference to where the completed work lives, e.g. a branch or commit.")]
        string resultReference,
        [Description("Optional in-band summary for your Lead: what you did/verified, evidence pointers, " +
                     "proposals (e.g. 'session X should run on profile Y'). NOT a substitute for the artifact — " +
                     "detail belongs in the workspace behind the reference. Capped at 16 KB.")]
        string? report = null,
        CancellationToken ct = default)
    {
        var caller = Caller;
        return Describe(await store.ApplyAsync(caller.Session, new ReportResult(caller, resultReference, report), ct));
    }

    [McpServerTool(Name = "request_input"),
     Description("Ask the Lead or a human for a decision that is above your scope. ALWAYS " +
                 "include 'question' — it is the only thing the answerer sees, so a request without " +
                 "it just says a session needs attention, not what for. A question ends your turn; the " +
                 "session stays up and the answer arrives as a follow-up — pull it on get_session. " +
                 "A permission request is a different tool (the harness relays it).")]
    public async Task<string> RequestInput(
        [Description("The kind of input needed: question, spawn_request, auth_help, endpoint_wait, or unreachable. " +
                     "This is a LABEL on the request, not a route: the plane does not use it to decide who may " +
                     "answer — a Lead or a human can answer any kind — and it does not say what you are asking. " +
                     "Put that in 'question'.")]
        string kind,
        [Description("What you are actually asking, in prose: the decision you cannot make, the options you see, " +
                     "your recommendation, and what you will do with each answer. Self-contained — the answerer " +
                     "may be a person who has not read your transcript. Capped at 16 KB; over-cap is refused and " +
                     "the session keeps working, so ask again shorter and point at the workspace for detail.")]
        string? question = null,
        CancellationToken ct = default)
    {
        if (!Enum.TryParse<InputRequestKind>(kind, ignoreCase: true, out var parsed))
            throw new McpException(
                $"unknown input kind '{kind}'; expected one of: {string.Join(", ", Enum.GetNames<InputRequestKind>())}");

        var caller = Caller;
        return Describe(await store.ApplyAsync(caller.Session, new RequestInput(caller, parsed, question), ct));
    }

    /// <summary>
    /// The default interval at which <c>request_permission</c> re-reads the row while it
    /// waits (§11 permission bridge). Half a second is imperceptible next to a human or a
    /// Lead deciding, and it is one indexed primary-key read per tick on a query that only
    /// runs while a worker is genuinely blocked.
    /// </summary>
    public static readonly TimeSpan DefaultPermissionPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>The poll interval from config, for tests that want millisecond granularity.</summary>
    private TimeSpan PermissionPollInterval =>
        int.TryParse(config["Landbridge:PermissionPollIntervalMs"], out var ms) && ms > 0
            ? TimeSpan.FromMilliseconds(ms)
            : DefaultPermissionPollInterval;

    // The harness's permission-prompt contract, verified against Claude Code 2.1.220 on the
    // wire rather than inferred. The request arrives as tool_name + input (+ tool_use_id);
    // the response is a PermissionResult serialized into this tool's text content —
    // {"behavior":"allow","updatedInput":{…}} or {"behavior":"deny","message":"…"}. The
    // parameter names are snake_case because the harness chose them: they are a wire shape
    // this tool has to match exactly, the same reason WorkerAssignment pins its own.
    private const string BehaviorAllow = "allow";
    private const string BehaviorDeny = "deny";

    [McpServerTool(Name = "request_permission"),
     Description("NOT FOR AGENTS TO CALL. This is the plane half of the permission bridge: ACP " +
                 "session/request_permission is answered by landbridged via POST /worker/permission, " +
                 "which runs the same code. The MCP tool remains for a harness that still hooks " +
                 "a prompt tool. Calling it yourself does nothing useful.")]
    public async Task<string> RequestPermission(
        [Description("The tool awaiting approval, as the harness names it.")]
        string tool_name,
        [Description("The arguments the harness proposes to call that tool with.")]
        JsonElement input,
        [Description("The harness's id for the tool call being approved, for correlation.")]
        string? tool_use_id = null,
        CancellationToken ct = default)
    {
        var caller = Caller;

        // The proposed input, verbatim and unparsed — this is agent-adjacent text that a
        // person is about to read in order to decide, so nothing here normalizes, truncates,
        // or prettifies it. The surfaces that render it escape and fence it (§13).
        var proposed = input.ValueKind == JsonValueKind.Undefined ? "{}" : input.GetRawText();

        var result = await PermissionRelay.OpenAndAwaitAsync(
            store, caller, tool_name, proposed, PermissionPollInterval, TimeProvider.System, ct,
            classifier);

        // v1 passes the proposed input through unchanged. An answerer who wanted a
        // different call would deny and say so, which is legible to the agent; silently
        // rewriting its arguments would not be.
        return result.Allow ? Allow(proposed) : Deny(result.Message);
    }

    /// <summary>An <c>allow</c> permission result carrying the harness's own proposed input back.</summary>
    private static string Allow(string proposedInputJson) =>
        $"{{\"behavior\":\"{BehaviorAllow}\",\"updatedInput\":{proposedInputJson}}}";

    /// <summary>
    /// A <c>deny</c> permission result. The message is what the agent actually reads, so it
    /// is the whole point of a denial rather than a decoration on it (§11).
    /// </summary>
    private static string Deny(string message) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["behavior"] = BehaviorDeny,
            ["message"] = message,
        });

    [McpServerTool(Name = "start_process"),
     Description("Run something long WITHOUT blocking: this returns as soon as the process is up, so you " +
                 "keep working while it runs. Use it for all long-running work — a build, a test suite, a " +
                 "dev server, a watcher, a migration, a REPL. Many harnesses have no way to background a " +
                 "command at all, so a shell call ties up your whole turn until it finishes; this is the " +
                 "mechanism that does not, and it behaves the same on every harness and every OS. You get a " +
                 "log path back, read it with ordinary file tools whenever you like, and check on the process " +
                 "when you choose. It also outlives you: landbridged supervises it as its own child, outside " +
                 "your session's process tree, so it survives your turn ending, you blocking on a question, " +
                 "this session being parked, and your harness being replaced — where anything you spawn " +
                 "yourself dies with the session. It is NOT restarted if it exits: the exit code is the " +
                 "result, and you (or the agent resumed later) decide what it means. A port is optional — " +
                 "plenty of long work listens on nothing. Never try to escape supervision by hand: no " +
                 "setsid, and never unset LANDBRIDGE_* on something you spawn.")]
    public async Task<StartProcessResult> StartProcess(
        [Description("A name for this process, unique on this machine: 1-64 characters of a-z, A-Z, 0-9, '-' or '_'.")]
        string name,
        [Description("The command as argv — the program first, then each argument separately. Never a " +
                     "shell string. Use absolute paths: the process gets landbridged's environment, not your shell's.")]
        string[] spawn,
        [Description("Directory to run in. Absolute. Defaults to landbridged's own working directory, which is " +
                     "probably not what you want.")]
        string? workingDirectory = null,
        [Description("Environment variables to set. Nothing is inherited implicitly, so pass PATH here if " +
                     "the command needs one.")]
        Dictionary<string, string>? env = null,
        [Description("Whether the process gets a stdin pipe you can write to. DEFAULT FALSE — most " +
                     "background work is fire-and-forget and is better off with nothing held open, and a " +
                     "program that reads stdin sees end-of-input immediately instead of blocking forever. " +
                     "Pass true only if you intend to write to it: write_process refuses on a process " +
                     "started without a pipe, and only a process with stdin can be stopped gracefully " +
                     "(otherwise stopping is a short wait and then a kill). Either way stdin is not a " +
                     "terminal, so programs that behave differently when stdin is not a TTY still will.")]
        bool openStdin = false,
        CancellationToken ct = default)
    {
        var caller = Caller;
        var result = await processes.StartAsync(
            caller.Session, name, spawn, workingDirectory, env, openStdin, ct);

        if (!result.Started)
            return new StartProcessResult(false, null, result.Refusal, null);

        // Uniform guidance, with no port branch: Landbridge tracks no port for a process, so
        // reachability is always a separate deliberate act (§8.2) rather than something this call
        // half-did. And nothing stops the process for you, which is the other thing an agent
        // reliably forgets.
        var next =
            "Landbridge does not track this process's port. If other sessions need to reach it, call " +
            "register_service with the name and the port it bound. Read its output at the log " +
            $"path, and stop it with stop_process when the work is done — nothing stops it for you." +
            (openStdin
                ? " Stdin is open, so you can write_process to it and stop_process can stop it gracefully."
                : " Started without stdin (the default): write_process will refuse, and stopping it is a hard stop. Restart it with openStdin true if you need to talk to it.");

        return new StartProcessResult(true, result.LogPath, null, next);
    }

    [McpServerTool(Name = "stop_process"),
     Description("Stop a background process on this machine by name. Any session dispatched to the machine " +
                 "may stop any process on it — including one an earlier session started, which is how a " +
                 "cleanup session tidies up. Processes are not cleaned up automatically when a session ends, so " +
                 "stopping what you started is part of finishing the work.")]
    public async Task<ProcessActionResult> StopProcess(
        [Description("The name the process was started with.")] string name,
        CancellationToken ct = default)
    {
        var r = await processes.StopAsync(Caller.Session, name, ct);
        return new ProcessActionResult(r.Ok, r.Refusal, r.Value);
    }

    [McpServerTool(Name = "list_processes"),
     Description("List what is running on your machine — the background processes agents started, " +
                 "and the operator's own declared services, each marked with its kind. Use it to pick " +
                 "a name that is not taken, to work out why a start was refused, and above all to find " +
                 "out what an earlier session left running when you have been sent to clean up. A service " +
                 "is the operator's and not yours to stop; a process is fair game for any session on the " +
                 "machine.")]
    public IReadOnlyList<RunningThing> ListProcesses() => processes.List(Caller.Session);

    [McpServerTool(Name = "write_process"),
     Description("Write text to a background process's stdin — a command for a REPL, an answer a tool is " +
                 "waiting for, or input for a script. IMPORTANT: this is a pipe, not a terminal. Programs " +
                 "that check for a TTY behave differently: they may not prompt, may buffer their output in " +
                 "blocks, or may refuse entirely, and a password prompt that reads /dev/tty will never see " +
                 "this. A curses or full-screen program will not work at all. Success means the pipe took " +
                 "the bytes, NOT that the program understood them — whatever it says back appears in the " +
                 "log file, so the loop is: write, read the log, decide.")]
    public async Task<ProcessActionResult> WriteProcess(
        [Description("The name the process was started with.")] string name,
        [Description("UTF-8 text to write. Capped at 16 KB per call; send several for more.")] string data,
        [Description("Append a newline, the default. Most programs read a line at a time, so leave this on " +
                     "unless you are deliberately sending a partial line.")]
        bool appendNewline = true,
        CancellationToken ct = default)
    {
        var r = await processes.WriteAsync(Caller.Session, name, data, appendNewline, ct);
        return new ProcessActionResult(r.Ok, r.Refusal, r.Value);
    }

    [McpServerTool(Name = "register_service"),
     Description("Advertise a live endpoint to other sessions in your Team. Bind the port first, " +
                 "then register — an entry pointing at a port you failed to bind sends consumers " +
                 "into the wrong process. One live registration per name in your Team: " +
                 "registering a name you already hold updates its port, and a name another session " +
                 "holds is refused, so pick a more specific one rather than retrying.")]
    public async Task<string> RegisterService(
        [Description("A name other sessions will use to find this service.")] string name,
        [Description("The loopback port you have already bound.")] int port,
        CancellationToken ct)
    {
        var caller = Caller;
        return Describe(await store.RegisterServiceAsync(caller, name, port, ct));
    }

    [McpServerTool(Name = "open_forward"),
     Description("Open a forward to another session's registered service in your Team (spec §8.3). Returns a " +
                 "loopback address — host and port — your client connects to directly; the tunnel to the " +
                 "remote service is stood up for you. Only services registered by a currently-working session " +
                 "in your Team are forwardable. The address is ready to use immediately and stays open for " +
                 "the life of the connection; the underlying grant expires in minutes but never severs an " +
                 "established connection.")]
    public async Task<OpenForwardResult> OpenForward(
        [Description("The name of a service registered by another session in your Team.")]
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

        // 2. Stand up both landbridged ends and wait for the consumer's bound loopback
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

    /// <summary>The loopback host the consumer's landbridged binds — never 0.0.0.0 (§8.3).</summary>
    public const string ForwardLoopbackHost = "127.0.0.1";

    [McpServerTool(Name = "open_preview"),
     Description("Mint a shareable browser preview URL for a service YOU registered on this session (spec §8.4). " +
                 "Returns an https URL a human can open in a browser with no landbridged install. Gated (default) " +
                 "requires the viewer to have a Landbridge operator session; public admits on the unguessable link " +
                 "alone and is always short-lived. Only a service you registered on this session with register_service " +
                 "is previewable. Hand the URL back in your report.")]
    public async Task<OpenPreviewResult> OpenPreview(
        [Description("The name of a service you registered on this session with register_service.")]
        string serviceName,
        [Description("Public preview: anyone with the link can open it (a capability URL, short mandatory TTL). " +
                     "Default false = gated, which requires a Landbridge operator session in the viewer's browser.")]
        bool isPublic = false,
        [Description("How long the preview stays live, in minutes. Public is capped short; gated defaults to a day. " +
                     "Omit for the default.")]
        int? ttlMinutes = null,
        CancellationToken ct = default)
    {
        var caller = Caller;
        var policy = isPublic ? PreviewAuthPolicy.Public : PreviewAuthPolicy.Gated;
        var ttl = PreviewMint.ResolveTtl(policy, ttlMinutes);

        // Session-scoped like open_forward is worker-scoped: only a service this session
        // registered is previewable (§8.4). A null result means it isn't yours.
        var mint = await previews.CreateForWorkerAsync(caller, serviceName, policy, ttl, ct)
            ?? throw new McpException(
                $"you have not registered a service named '{serviceName}' on this session; register it with " +
                "register_service first (a preview only ever exposes your own session's service).");

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
/// mechanics that stood the tunnel up are held by landbridged and deliberately kept
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

/// <summary>What a worker learns from <c>start_process</c> (§10). No port: this is a process
/// manager, and reachability is §8.2's noun.</summary>
/// <param name="LogPath">Where the machine captured this run's output. The agent is on that
/// machine, so it reads its own process's output with ordinary file tools — no serving path and
/// no redaction question (§16 open question 8).</param>
/// <param name="NextStep">What to do now — in particular that starting is not registering, and
/// that nothing stops this process for you.</param>
public sealed record StartProcessResult(
    bool Started, string? LogPath, string? Refusal, string? NextStep);

/// <summary>The result of <c>stop_process</c> or <c>write_process</c> (§10).
/// <see cref="Value"/> is the exit code for a stop, or the bytes accepted for a write.</summary>
public sealed record ProcessActionResult(bool Ok, string? Refusal, int? Value);

/// <summary>
/// What <c>open_preview</c> hands back (spec §8.4): the shareable <see cref="Url"/>
/// to put in a report, the <see cref="Auth"/> policy (<c>gated</c>|<c>public</c>)
/// so the worker knows whether a viewer needs an operator session, and
/// <see cref="ExpiresAt"/> when the preview stops admitting new connections.
/// snake_case-pinned like <see cref="OpenForwardResult"/>.
/// </summary>
public sealed record OpenPreviewResult(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("auth")] string Auth,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);
