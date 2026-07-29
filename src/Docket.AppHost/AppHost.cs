// Docket's local development inner loop. This is a dev-time orchestrator only —
// NOT a production deployment path. In production docketd runs standalone on
// each machine (§10, restart=reboot); nothing here couples the runtime to
// Aspire. What one `dotnet run` here buys you: a managed Postgres, the
// MCP/control-plane host wired to it, and a real docketd runner enrolled and
// connected back — the full Lead → plane → runner → worker loop, with the
// Aspire dashboard capturing the host's OpenTelemetry traces, logs, and metrics
// (the cross-process insight §1 is built around).
//
// The runner and its spawned worker are in the loop now: docketd enrolls (via a
// dev-seeded machine token, below), dials the plane's /runner endpoint, and
// stands ready. It does NOT auto-create a task — this is a standing fleet; a
// human Lead creates work over MCP, exactly as in production.
//
// Scope note: OTel on docketd/the worker is out of scope (docketd is not a Host
// builder, so it gets no ServiceDefaults); its console logs stream to the
// dashboard as a resource. Propagating runner traces is a follow-up.
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

var builder = DistributedApplication.CreateBuilder(args);

// The plane's fixed dev URL. Sibling processes reach it directly at this known
// address: docketd dials ws://127.0.0.1:5000/runner, and the spawned worker
// dials http://127.0.0.1:5000/ (DispatchService.DefaultPublicMcpUrl). Keeping it
// fixed and un-proxied (below) means those siblings need no Aspire awareness.
const string mcpHost = "127.0.0.1";
const int mcpPort = 5000;
var mcpUrl = $"http://{mcpHost}:{mcpPort}";

// The relay's fixed dev URL (spec §8.3). Pinned and UN-proxied like the plane:
// docketd's two data planes dial ws://127.0.0.1:5100/tunnel directly at this
// known loopback address, so an ephemeral DCP proxy port would be invisible to
// them. The worker never learns it — the plane injects it per open_forward
// (WorkerTools reads Docket:RelayUrl, set on the mcp resource below).
const string relayHost = "127.0.0.1";
const int relayPort = 5100;
var relayUrl = $"http://{relayHost}:{relayPort}";

// One shared bearer per AppHost run authenticates the relay to the plane's
// /relay/validate endpoint (§8.3). Minted fresh here and handed to exactly the
// two hosts that need it — the plane checks it (Docket:RelayValidation:Bearer),
// the relay presents it (Relay:ControlPlane:Bearer) — never persisted. Setting
// it, together with Relay:ControlPlane:Url below, makes ControlPlaneGrantValidator
// the ACTIVE validator in the dev loop; the fail-closed StaticSecretGrantValidator
// must not be what stands (a tunnel spliced on an unvalidated grant is the §13 risk).
var relayValidationBearer = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

// A per-run scratch area under the temp dir: the machine-token seed file the MCP
// host writes and docketd reads, and docketd's work_root for per-task dirs.
var runDir = Directory.CreateDirectory(
    Path.Combine(Path.GetTempPath(), "docket-apphost")).FullName;
var seedTokenFile = Path.Combine(runDir, "machine-token.json");
var workRoot = Directory.CreateDirectory(Path.Combine(runDir, "work")).FullName;

// A managed Postgres container with a persistent data volume, so the schema and
// data survive across `dotnet run`s. (The ephemeral initdb cluster the tests
// spin up is a separate, test-only concern — see PostgresFixture.) The database
// is named `docket` to match the connection-string key the MCP host already
// reads via GetConnectionString("Docket").
var docketDb = builder.AddPostgres("postgres")
    .WithDataVolume()
    .AddDatabase("docket");

// The MCP + control-plane host. WithReference injects Postgres as
// ConnectionStrings__docket; WaitFor holds startup until Postgres is healthy;
// MigrateOnStartup applies the checked-in EF migration; DevSeed:TokenFile tells
// the host to bootstrap a machine identity and write its token where docketd
// picks it up (all three are dev-loop-only signals production never sets).
//
// The endpoint is pinned to a fixed port and NOT proxied by DCP: the sibling
// docketd/worker processes dial 127.0.0.1:5000 directly, so an ephemeral proxy
// port in front of Kestrel would be invisible to them. ASPNETCORE_URLS binds
// Kestrel to exactly that loopback address. WithHttpHealthCheck makes WaitFor
// (below) gate docketd's start on the host actually serving — which is strictly
// after the seed file has been written, since that write precedes app.Run().
//
// ExcludeLaunchProfile drops the launchSettings-derived endpoints (the project's
// http:5115 / https profile): we want exactly one endpoint on the fixed port,
// and a leftover https endpoint with no bound port makes DCP fail to reconcile
// the resource — which would stall it short of healthy and hang WaitFor(mcp).
// ASPNETCORE_ENVIRONMENT is then set explicitly so the host still maps /health
// (the ServiceDefaults health endpoints are Development-only).
var mcp = builder.AddProject<Projects.Docket_Mcp>("mcp", options => options.ExcludeLaunchProfile = true)
    .WithReference(docketDb)
    .WaitFor(docketDb)
    .WithHttpEndpoint(port: mcpPort, targetPort: mcpPort, isProxied: false)
    .WithEnvironment("ASPNETCORE_URLS", mcpUrl)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("Docket__MigrateOnStartup", "true")
    .WithEnvironment("Docket__DevSeed__TokenFile", seedTokenFile)
    // §8.3: the shared bearer the plane's /relay/validate endpoint requires (fail-
    // closed 503 without it), and the relay URL WorkerTools hands docketd per
    // open_forward. Both are read from IConfiguration by Docket.Mcp; set as env.
    .WithEnvironment("Docket__RelayValidation__Bearer", relayValidationBearer)
    .WithEnvironment("Docket__RelayUrl", relayUrl)
    .WithHttpHealthCheck("/health");

// docket-relay as a dev-loop resource (§8.3). Same fixed, un-proxied endpoint
// treatment as the plane: docketd dials 127.0.0.1:5100 directly, so an ephemeral
// DCP proxy port would be invisible. ExcludeLaunchProfile drops the launchSettings
// endpoints for the one fixed port; ASPNETCORE_URLS binds Kestrel there and
// ASPNETCORE_ENVIRONMENT=Development makes ServiceDefaults map /health (the
// WithHttpHealthCheck gate). Relay:ControlPlane:Url points the validator at the
// plane — so ControlPlaneGrantValidator, not the static stub, is active — and it
// presents the shared bearer on /relay/validate. WaitFor(mcp): the validator
// calls the plane, so the plane must be serving first.
builder.AddProject<Projects.Docket_Relay>("relay", options => options.ExcludeLaunchProfile = true)
    .WaitFor(mcp)
    .WithHttpEndpoint(port: relayPort, targetPort: relayPort, isProxied: false)
    .WithEnvironment("ASPNETCORE_URLS", relayUrl)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("Relay__ControlPlane__Url", mcpUrl)
    .WithEnvironment("Relay__ControlPlane__Bearer", relayValidationBearer)
    .WithHttpHealthCheck("/health");

// docketd's config: read the committed template, resolve the two AppHost-owned
// placeholders (work_root + the built worker harness path), and write it out for
// docketd to load. {mcp_config} is deliberately left in place — docketd's
// ProcessSupervisor substitutes it per task with the generated mcp.json (§13).
var resolvedConfigPath = WriteResolvedDocketdConfig(workRoot);

// docketd itself, as an Aspire project resource (Projects.Docket_Runner comes
// from the IsAspireProjectResource ProjectReference in the .csproj). It waits
// for the MCP host to be healthy, then an environment callback reads the seed
// file and hands docketd its machine token + id and the control-plane URL. The
// callback runs at docketd's start, which WaitFor gates behind mcp being
// healthy — so the seed file is present by then.
builder.AddProject<Projects.Docket_Runner>("docketd")
    .WithArgs("--config", resolvedConfigPath)
    .WaitFor(mcp)
    .WithEnvironment(async ctx =>
    {
        ctx.EnvironmentVariables["DOCKET_CONTROL_URL"] = $"ws://{mcpHost}:{mcpPort}/runner";

        // Belt-and-suspenders against a startup race: WaitFor(mcp) should already
        // guarantee the seed file exists, but poll briefly rather than throw if
        // the health gate and the write ever interleave.
        var seed = await ReadSeedWithRetryAsync(seedTokenFile, TimeSpan.FromSeconds(30));
        ctx.EnvironmentVariables["DOCKET_MACHINE_TOKEN"] = seed.MachineToken;
        ctx.EnvironmentVariables["DOCKET_MACHINE_ID"] = seed.MachineId;
    });

builder.Build().Run();

// Reads src/Docket.AppHost/docketd.dev.json (copied beside the AppHost assembly),
// substitutes {work_root} and {worker_harness}, and writes the resolved config to
// a temp file, returning its path. JSON string values are substituted via the
// serializer so any path characters are escaped correctly; {mcp_config} is left
// untouched for docketd to fill per task.
string WriteResolvedDocketdConfig(string resolvedWorkRoot)
{
    var templatePath = Path.Combine(AppContext.BaseDirectory, "docketd.dev.json");
    var template = File.ReadAllText(templatePath);
    var resolved = template
        .Replace("\"{work_root}\"", JsonSerializer.Serialize(resolvedWorkRoot), StringComparison.Ordinal)
        .Replace("\"{worker_harness}\"", JsonSerializer.Serialize(ResolveWorkerHarnessPath()), StringComparison.Ordinal);

    var outPath = Path.Combine(runDir, "docketd.resolved.json");
    File.WriteAllText(outPath, resolved);
    return outPath;
}

// The absolute path to the built Docket.WorkerHarness apphost, resolved from the
// AppHost's OWN build output → the sibling test project's bin (mirroring
// WalkingSkeletonEndToEndTests.WorkerHarnessPath). A build-only ProjectReference
// in the .csproj guarantees the harness is built first; here we only need its
// path. The harness is resolved from its own bin (not copied beside the AppHost)
// because its MCP-client dependency closure is copied local only there.
static string ResolveWorkerHarnessPath()
{
    const string stem = "Docket.WorkerHarness";
    var appHostDir = AppContext.BaseDirectory; // .../src/Docket.AppHost/bin/<config>/net10.0/
    var harnessDir = appHostDir.Replace(
        Path.Combine("src", "Docket.AppHost", "bin"),
        Path.Combine("tests", stem, "bin"),
        StringComparison.Ordinal);
    var apphost = Path.Combine(harnessDir, OperatingSystem.IsWindows() ? stem + ".exe" : stem);
    return File.Exists(apphost)
        ? apphost
        : throw new FileNotFoundException(
            $"worker harness apphost not found at {apphost}; is Docket.WorkerHarness built?");
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
