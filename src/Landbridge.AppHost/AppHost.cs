// Landbridge's local development inner loop. This is a dev-time orchestrator only —
// NOT a production deployment path. In production landbridged runs standalone on
// each machine (§10, restart=reboot); nothing here couples the runtime to
// Aspire. What one `dotnet run` here buys you: a managed Postgres, the
// MCP/control-plane host wired to it, and a real landbridged runner enrolled and
// connected back — the full Lead → plane → runner → worker loop, with the
// Aspire dashboard capturing the host's OpenTelemetry traces, logs, and metrics
// (the cross-process insight §1 is built around).
//
// The runner and its spawned worker are in the loop now: landbridged enrolls (via a
// dev-seeded machine token, below), dials the plane's /runner endpoint, and
// stands ready. It does NOT auto-create a task — this is a standing fleet; a
// human Lead creates work over MCP, exactly as in production.
//
// Scope note: OTel on landbridged/the worker is out of scope (landbridged is not a Host
// builder, so it gets no ServiceDefaults); its console logs stream to the
// dashboard as a resource. Propagating runner traces is a follow-up.
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

var builder = DistributedApplication.CreateBuilder(args);

// The plane's fixed dev URL. Sibling processes reach it directly at this known
// address: landbridged dials ws://127.0.0.1:5050/runner, and the spawned worker
// dials http://127.0.0.1:5050/ (DispatchService.DefaultPublicMcpUrl). Keeping it
// fixed and un-proxied (below) means those siblings need no Aspire awareness.
const string mcpHost = "127.0.0.1";
// NOT 5000, and not 7000 either: macOS ControlCenter binds both for AirPlay
// Receiver, on *every* interface. Kestrel here binds 127.0.0.1 only, so the two
// coexist -- but Aspire's WithHttpHealthCheck resolves via localhost, gets ::1
// first, reaches AirPlay instead of the plane, and reads its 403 as unhealthy.
// WaitFor(mcp) then never releases and relay, preview and landbridged never
// start, with nothing in any log to say why.
const int mcpPort = 5050;
var mcpUrl = $"http://{mcpHost}:{mcpPort}";

// The relay's fixed dev URL (spec §8.3). Pinned and UN-proxied like the plane:
// landbridged's two data planes dial ws://127.0.0.1:5100/tunnel directly at this
// known loopback address, so an ephemeral DCP proxy port would be invisible to
// them. The worker never learns it — the plane injects it per open_forward
// (WorkerTools reads Landbridge:RelayUrl, set on the mcp resource below).
const string relayHost = "127.0.0.1";
const int relayPort = 5100;
var relayUrl = $"http://{relayHost}:{relayPort}";

// One shared bearer per AppHost run authenticates the relay to the plane's
// /relay/validate endpoint (§8.3). Minted fresh here and handed to exactly the
// two hosts that need it — the plane checks it (Landbridge:RelayValidation:Bearer),
// the relay presents it (Relay:ControlPlane:Bearer) — never persisted. Setting
// it, together with Relay:ControlPlane:Url below, makes ControlPlaneGrantValidator
// the ACTIVE validator in the dev loop; the fail-closed StaticSecretGrantValidator
// must not be what stands (a tunnel spliced on an unvalidated grant is the §13 risk).
var relayValidationBearer = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

// The HTTP preview frontend's dev wiring (§8.4). Its raw TcpListener owns a fixed
// browser-facing port; its Kestrel (health only) sits on a separate fixed port.
// preview.localhost (and any *.preview.localhost label) resolves to loopback, so a
// browser reaches http://{label}.preview.localhost:5200 directly. One shared bearer
// authenticates the frontend to the plane's /preview/connect + /preview/exchange
// (the plane checks Landbridge:PreviewConnect:Bearer; the frontend presents it).
const string previewDomain = "preview.localhost";
const int previewPort = 5200;
const int previewHealthPort = 5202;
var previewUrlBase = $"http://{previewDomain}:{previewPort}";
var previewHealthUrl = $"http://127.0.0.1:{previewHealthPort}";
var previewListenPort = previewPort.ToString();
var previewConnectBearer = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

// A per-run scratch area under the temp dir: the machine-token seed file the MCP
// host writes and landbridged reads, and landbridged's work_root for per-task dirs.
var runDir = Directory.CreateDirectory(
    Path.Combine(Path.GetTempPath(), "landbridge-apphost")).FullName;
var seedTokenFile = Path.Combine(runDir, "machine-token.json");
var workRoot = Directory.CreateDirectory(Path.Combine(runDir, "work")).FullName;

// A managed Postgres container with a persistent data volume, so the schema and
// data survive across `dotnet run`s. (The ephemeral initdb cluster the tests
// spin up is a separate, test-only concern — see PostgresFixture.) The database
// is named `landbridge` to match the connection-string key the MCP host already
// reads via GetConnectionString("Landbridge").
var landbridgeDb = builder.AddPostgres("postgres")
    .WithDataVolume()
    .AddDatabase("landbridge");

// The MCP + control-plane host. WithReference injects Postgres as
// ConnectionStrings__landbridge; WaitFor holds startup until Postgres is healthy;
// MigrateOnStartup applies the checked-in EF migration; DevSeed:TokenFile tells
// the host to bootstrap a machine identity and write its token where landbridged
// picks it up (all three are dev-loop-only signals production never sets).
//
// The endpoint is pinned to a fixed port and NOT proxied by DCP: the sibling
// landbridged/worker processes dial 127.0.0.1:5050 directly, so an ephemeral proxy
// port in front of Kestrel would be invisible to them. ASPNETCORE_URLS binds
// Kestrel to exactly that loopback address. WithHttpHealthCheck makes WaitFor
// (below) gate landbridged's start on the host actually serving — which is strictly
// after the seed file has been written, since that write precedes app.Run().
//
// ExcludeLaunchProfile drops the launchSettings-derived endpoints (the project's
// http:5115 / https profile): we want exactly one endpoint on the fixed port,
// and a leftover https endpoint with no bound port makes DCP fail to reconcile
// the resource — which would stall it short of healthy and hang WaitFor(mcp).
// ASPNETCORE_ENVIRONMENT is then set explicitly so the host still maps /health
// (the ServiceDefaults health endpoints are Development-only).
var mcp = builder.AddProject<Projects.Landbridge_Mcp>("mcp", options => options.ExcludeLaunchProfile = true)
    .WithReference(landbridgeDb)
    .WaitFor(landbridgeDb)
    .WithHttpEndpoint(port: mcpPort, targetPort: mcpPort, isProxied: false)
    .WithEnvironment("ASPNETCORE_URLS", mcpUrl)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("Landbridge__MigrateOnStartup", "true")
    .WithEnvironment("Landbridge__DevSeed__TokenFile", seedTokenFile)
    // §8.3: the shared bearer the plane's /relay/validate endpoint requires (fail-
    // closed 503 without it), and the relay URL WorkerTools hands landbridged per
    // open_forward. Both are read from IConfiguration by Landbridge.Mcp; set as env.
    .WithEnvironment("Landbridge__RelayValidation__Bearer", relayValidationBearer)
    .WithEnvironment("Landbridge__RelayUrl", relayUrl)
    // §8.4 preview: the shared bearer the plane's /preview/connect + /preview/exchange
    // require, and the wildcard base the plane builds preview URLs onto (open_preview
    // + the dashboard mint read Landbridge:PreviewUrlBase).
    .WithEnvironment("Landbridge__PreviewConnect__Bearer", previewConnectBearer)
    .WithEnvironment("Landbridge__PreviewUrlBase", previewUrlBase)
    .WithHttpHealthCheck("/health");

// landbridge-relay as a dev-loop resource (§8.3). Same fixed, un-proxied endpoint
// treatment as the plane: landbridged dials 127.0.0.1:5100 directly, so an ephemeral
// DCP proxy port would be invisible. ExcludeLaunchProfile drops the launchSettings
// endpoints for the one fixed port; ASPNETCORE_URLS binds Kestrel there and
// ASPNETCORE_ENVIRONMENT=Development makes ServiceDefaults map /health (the
// WithHttpHealthCheck gate). Relay:ControlPlane:Url points the validator at the
// plane — so ControlPlaneGrantValidator, not the static stub, is active — and it
// presents the shared bearer on /relay/validate. WaitFor(mcp): the validator
// calls the plane, so the plane must be serving first.
builder.AddProject<Projects.Landbridge_Relay>("relay", options => options.ExcludeLaunchProfile = true)
    .WaitFor(mcp)
    .WithHttpEndpoint(port: relayPort, targetPort: relayPort, isProxied: false)
    .WithEnvironment("ASPNETCORE_URLS", relayUrl)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("Relay__ControlPlane__Url", mcpUrl)
    .WithEnvironment("Relay__ControlPlane__Bearer", relayValidationBearer)
    .WithHttpHealthCheck("/health");

// landbridge-preview as a dev-loop resource (§8.4). Two listeners: a raw TcpListener on
// the fixed browser port (Preview:ListenPort — a browser hits
// http://{label}.preview.localhost:5200) and Kestrel on a separate fixed port for
// health only (ASPNETCORE_URLS + WithHttpHealthCheck). Plaintext (no cert) in dev —
// production supplies a wildcard PEM. It calls the plane's /preview/connect +
// /preview/exchange with the shared bearer, and redirects gated browsers to the
// dashboard origin (mcpUrl) to confirm. WaitFor(mcp): it calls the plane on connect.
builder.AddProject<Projects.Landbridge_Preview>("preview", options => options.ExcludeLaunchProfile = true)
    .WaitFor(mcp)
    .WithHttpEndpoint(port: previewHealthPort, targetPort: previewHealthPort, isProxied: false)
    .WithEnvironment("ASPNETCORE_URLS", previewHealthUrl)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("Preview__ListenPort", previewListenPort)
    .WithEnvironment("Preview__Domain", previewDomain)
    .WithEnvironment("Preview__ControlPlaneUrl", mcpUrl)
    .WithEnvironment("Preview__DashboardUrl", mcpUrl)
    .WithEnvironment("Preview__ControlPlaneBearer", previewConnectBearer)
    .WithHttpHealthCheck("/health");

// landbridged's config: read the committed template, resolve the two AppHost-owned
// placeholders (work_root + the built worker harness path), and write it out for
// landbridged to load. {mcp_config} is deliberately left in place — landbridged's
// ProcessSupervisor substitutes it per task with the generated mcp.json (§13).
var resolvedConfigPath = WriteResolvedLandbridgedConfig(workRoot);

// landbridged itself, as an Aspire project resource (Projects.Landbridge_Runner comes
// from the IsAspireProjectResource ProjectReference in the .csproj). It waits
// for the MCP host to be healthy, then an environment callback reads the seed
// file and hands landbridged its machine token + id and the control-plane URL. The
// callback runs at landbridged's start, which WaitFor gates behind mcp being
// healthy — so the seed file is present by then.
builder.AddProject<Projects.Landbridge_Runner>("landbridged")
    .WithArgs("--config", resolvedConfigPath)
    .WaitFor(mcp)
    .WithEnvironment(async ctx =>
    {
        ctx.EnvironmentVariables["LANDBRIDGE_CONTROL_URL"] = $"ws://{mcpHost}:{mcpPort}/runner";

        // Belt-and-suspenders against a startup race: WaitFor(mcp) should already
        // guarantee the seed file exists, but poll briefly rather than throw if
        // the health gate and the write ever interleave.
        var seed = await ReadSeedWithRetryAsync(seedTokenFile, TimeSpan.FromSeconds(30));
        ctx.EnvironmentVariables["LANDBRIDGE_MACHINE_TOKEN"] = seed.MachineToken;
        ctx.EnvironmentVariables["LANDBRIDGE_MACHINE_ID"] = seed.MachineId;
    });

// Completion is Lead-adjudicated (§7, §9 check 4): a Lead session's submit_review
// verdict completes a `lead`-mode task, so there is no separate verifier resource in
// the loop. CI and tests are evidence a Lead gathers itself, not a verdict actor.
// The dev loop is no longer zero-human by construction — a human-driven Lead closes
// the lifecycle — which is the point of the realignment.

builder.Build().Run();

// Reads src/Landbridge.AppHost/landbridged.dev.json (copied beside the AppHost assembly),
// substitutes {work_root} and {worker_harness}, and writes the resolved config to
// a temp file, returning its path. JSON string values are substituted via the
// serializer so any path characters are escaped correctly; {mcp_config} is left
// untouched for landbridged to fill per task.
string WriteResolvedLandbridgedConfig(string resolvedWorkRoot)
{
    var templatePath = Path.Combine(AppContext.BaseDirectory, "landbridged.dev.json");
    var template = File.ReadAllText(templatePath);
    var resolved = template
        .Replace("\"{work_root}\"", JsonSerializer.Serialize(resolvedWorkRoot), StringComparison.Ordinal)
        .Replace("\"{worker_harness}\"", JsonSerializer.Serialize(ResolveWorkerHarnessPath()), StringComparison.Ordinal);

    var outPath = Path.Combine(runDir, "landbridged.resolved.json");
    File.WriteAllText(outPath, resolved);
    return outPath;
}

// The absolute path to the built Landbridge.WorkerHarness apphost, resolved from the
// AppHost's OWN build output → the sibling test project's bin (mirroring
// WalkingSkeletonEndToEndTests.WorkerHarnessPath). A build-only ProjectReference
// in the .csproj guarantees the harness is built first; here we only need its
// path. The harness is resolved from its own bin (not copied beside the AppHost)
// because its MCP-client dependency closure is copied local only there.
static string ResolveWorkerHarnessPath()
{
    const string stem = "Landbridge.WorkerHarness";
    var appHostDir = AppContext.BaseDirectory; // .../src/Landbridge.AppHost/bin/<config>/net10.0/
    var harnessDir = appHostDir.Replace(
        Path.Combine("src", "Landbridge.AppHost", "bin"),
        Path.Combine("tests", stem, "bin"),
        StringComparison.Ordinal);
    var apphost = Path.Combine(harnessDir, OperatingSystem.IsWindows() ? stem + ".exe" : stem);
    return File.Exists(apphost)
        ? apphost
        : throw new FileNotFoundException(
            $"worker harness apphost not found at {apphost}; is Landbridge.WorkerHarness built?");
}

static async Task<(string MachineId, string MachineToken)> ReadSeedWithRetryAsync(string path, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (true)
    {
        if (File.Exists(path))
        {
            try
            {
                var node = JsonNode.Parse(await File.ReadAllTextAsync(path));
                var id = (string?)node?["machineId"];
                var token = (string?)node?["machineToken"];
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(token))
                    return (id, token);
            }
            catch (JsonException) { /* mid-write; retry */ }
        }
        if (DateTime.UtcNow >= deadline)
            throw new InvalidOperationException($"dev seed token file never appeared/parsed: {path}");
        await Task.Delay(200);
    }
}

