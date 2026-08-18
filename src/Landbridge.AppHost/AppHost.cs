// Landbridge's local development inner loop. This is a dev-time orchestrator only —
// NOT a production deployment path. In production landbridged runs standalone on
// each machine (§10, restart=reboot); nothing here couples the runtime to
// Aspire. What one `dotnet run` here buys you: a managed Postgres, the
// MCP/control-plane host wired to it, and a real landbridged runner enrolled and
// connected back — the full Lead → plane → runner → worker loop, with the
// Aspire dashboard capturing the host's OpenTelemetry traces, logs, and metrics
// (the cross-process insight §1 is built around).
//
// Three landbridged boxes enroll (via dev-seeded tokens, below), dial /runner,
// and stand ready: Codex, Claude, and Grok on this host's real OS. Each box spawns
// the real ACP harness (`codex-acp`, `claude-agent-acp`, `grok agent stdio`).
// Provider keys come from AppHost user secrets, the MultiMachine secrets id
// (so the same local store the paid e2e uses also feeds this loop), or the
// process environment — stamped on that box's landbridged so the child inherits
// them. They never go in the runner config. No Team is minted — a human Lead
// creates work over MCP, exactly as in production.
//
// landbridged is not a Host builder, so it gets no ServiceDefaults traces.
// The Claude box opts into harness OTLP (`telemetry.otel`) so token/cost
// metrics can land in the Aspire dashboard. Console logs stream as a resource.
using Landbridge.ControlPlane;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

var builder = DistributedApplication.CreateBuilder(args);

// AppHost's own UserSecretsId is already in the configuration. Also load the
// MultiMachine test secrets so a key stored for the paid e2e is reused here
// without a second `dotnet user-secrets set`. Last-added would override env,
// so FirstNonEmpty still prefers the process environment.
builder.Configuration.AddJsonFile(UserSecretsPath(DevBoxConfig.MultiMachineSecretsId), optional: true, reloadOnChange: false);

var providerKeys = new DevBoxConfig.ProviderKeys(
    Anthropic: DevBoxConfig.FirstNonEmpty(builder.Configuration, "ANTHROPIC_API_KEY", "ANTHROPIC_KEY"),
    Codex: DevBoxConfig.FirstNonEmpty(builder.Configuration, "CODEX_API_KEY", "OPENAI_API_KEY", "OPENAI_KEY"),
    Xai: DevBoxConfig.FirstNonEmpty(builder.Configuration, "XAI_API_KEY", "XAI_KEY"));

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
const int classifierPort = 5310;
var previewUrlBase = $"http://{previewDomain}:{previewPort}";
var previewHealthUrl = $"http://127.0.0.1:{previewHealthPort}";
var previewListenPort = previewPort.ToString();
var previewConnectBearer = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

// A per-run scratch area under the temp dir: one seed file per enrolled box
// (written by the plane) and per-box work_root / state-dir for landbridged.
var runDir = Directory.CreateDirectory(
    Path.Combine(Path.GetTempPath(), "landbridge-apphost")).FullName;
var seedDir = Directory.CreateDirectory(Path.Combine(runDir, "seed")).FullName;

// Keep in lockstep with Landbridge.Mcp Program.cs DevSeed harness list.
string[] devHarnesses = ["codex", "claude", "grok"];
var missingKeys = devHarnesses
    .Where(h => providerKeys.For(h) is null)
    .Select(h => $"{DevSeedNaming.Box(h)} ({DevBoxConfig.CanonicalKeyName(h)})")
    .ToArray();
if (missingKeys.Length > 0)
    throw new InvalidOperationException(
        "landbridge-apphost: missing provider key(s) for " +
        string.Join(", ", missingKeys) +
        ". Set them as user secrets on src/Landbridge.AppHost or " +
        "tests/Landbridge.MultiMachine.Tests, or in the environment.");

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
// MigrateOnStartup applies the checked-in EF migration; DevSeed:TokenDir tells
// the host to enroll the Codex/Claude/Grok linux boxes and write each token
// where that box's landbridged picks it up (dev-loop-only; production never sets it).
//
// The endpoint is pinned to a fixed port and NOT proxied by DCP: the sibling
// landbridged/worker processes dial 127.0.0.1:5050 directly, so an ephemeral proxy
// port in front of Kestrel would be invisible to them. ASPNETCORE_URLS binds
// Kestrel to exactly that loopback address. WithHttpHealthCheck makes WaitFor
// (below) gate landbridged's start on the host actually serving — which is strictly
// after the seed file has been written, since that write precedes app.Run().
//
// ExcludeLaunchProfile drops the launchSettings-derived endpoints (the project's
// http:5050 / https profile): we want exactly one endpoint on the fixed port,
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
    .WithEnvironment("Landbridge__DevSeed__TokenDir", seedDir)
    // §8.3: the shared bearer the plane's /relay/validate endpoint requires (fail-
    // closed 503 without it), and the relay URL WorkerTools hands landbridged per
    // open_forward. Both are read from IConfiguration by Landbridge.Mcp; set as env.
    .WithEnvironment("Landbridge__RelayValidation__Bearer", relayValidationBearer)
    .WithEnvironment("Landbridge__RelayUrl", relayUrl)
    .WithEnvironment("Landbridge__Classifier__Url", "http://127.0.0.1:" + classifierPort)
    // §8.4 preview: the shared bearer the plane's /preview/connect + /preview/exchange
    // require, and the wildcard base the plane builds preview URLs onto (open_preview
    // + the dashboard mint read Landbridge:PreviewUrlBase).
    .WithEnvironment("Landbridge__PreviewConnect__Bearer", previewConnectBearer)
    .WithEnvironment("Landbridge__PreviewUrlBase", previewUrlBase)
    // Dashboard / OAuth login: Development appsettings already hash the passphrase
    // `dev`. Set it here too so an override of ASPNETCORE_ENVIRONMENT cannot
    // silently fail-close the only human door in this loop.
    .WithEnvironment("Landbridge__Operator__PassphraseHash",
        Convert.ToHexString(SHA256.HashData("dev"u8)))
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

// Qwen permission classifier. Un-proxied so the plane can dial 127.0.0.1:5310.
// Key + model are required for *this* resource: the sidecar exits 1 without
// them and Aspire marks it failed. Do not throw here and do not WaitFor it —
// the rest of the loop still starts, and a down classifier is Ask on the plane.
var repoRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".."));
var classifier = builder.AddDockerfile("classifier", Path.Combine(repoRoot, "src/Landbridge.Classifier"), "Dockerfile")
    .WithHttpEndpoint(port: classifierPort, targetPort: classifierPort, isProxied: false)
    .WithEnvironment("PORT", classifierPort.ToString())
    .WithHttpHealthCheck("/health");
var classifierKey = DevBoxConfig.FirstNonEmpty(
    builder.Configuration,
    "LANDBRIDGE_CLASSIFIER_API_KEY",
    "DASHSCOPE_API_KEY",
    "OPENAI_API_KEY");
if (classifierKey is { Length: > 0 })
    classifier.WithEnvironment("LANDBRIDGE_CLASSIFIER_API_KEY", classifierKey);
var classifierModel = DevBoxConfig.FirstNonEmpty(
    builder.Configuration, "LANDBRIDGE_CLASSIFIER_MODEL");
if (classifierModel is { Length: > 0 })
    classifier.WithEnvironment("LANDBRIDGE_CLASSIFIER_MODEL", classifierModel);
var classifierBase = DevBoxConfig.FirstNonEmpty(
    builder.Configuration, "LANDBRIDGE_CLASSIFIER_BASE_URL");
if (classifierBase is { Length: > 0 })
    classifier.WithEnvironment("LANDBRIDGE_CLASSIFIER_BASE_URL", classifierBase);
mcp.WithEnvironment("Landbridge__Classifier__TimeoutMs", "45000");

// One landbridged per seeded box. Each has its own config, work_root, and
// --state-dir so credentials and transcripts do not collide. WaitFor(mcp)
// gates start until the plane has written that box's seed file.
foreach (var harness in devHarnesses)
{
    var box = DevSeedNaming.Box(harness);
    var profile = DevSeedNaming.Profile(harness);
    var boxWork = Directory.CreateDirectory(Path.Combine(runDir, "work", box)).FullName;
    var boxState = Directory.CreateDirectory(Path.Combine(runDir, "state", box)).FullName;
    var configPath = DevBoxConfig.Write(runDir, boxWork, harness, profile);
    var seedPath = Path.Combine(seedDir, $"{box}.json");

    builder.AddProject<Projects.Landbridge_Runner>($"landbridged-{harness}")
        .WithArgs("--config", configPath, "--state-dir", boxState)
        .WaitFor(mcp)
        .WithEnvironment(async ctx =>
        {
            ctx.EnvironmentVariables["LANDBRIDGE_CONTROL_URL"] = $"ws://{mcpHost}:{mcpPort}/runner";
            var seed = await ReadSeedWithRetryAsync(seedPath, TimeSpan.FromSeconds(30));
            ctx.EnvironmentVariables["LANDBRIDGE_MACHINE_TOKEN"] = seed.MachineToken;
            ctx.EnvironmentVariables["LANDBRIDGE_MACHINE_ID"] = seed.MachineId;
            // landbridged does not expand `{env:…}` in profile env. Stamp the
            // canonical name this harness reads so the ACP child inherits it.
            ctx.EnvironmentVariables[DevBoxConfig.CanonicalKeyName(harness)] =
                providerKeys.For(harness)!;
        });
}

// Completion is Lead-adjudicated (§7, §9 check 4): a Lead session's submit_review
// verdict completes a `lead`-mode task, so there is no separate verifier resource in
// the loop. CI and tests are evidence a Lead gathers itself, not a verdict actor.
// The dev loop is no longer zero-human by construction — a human-driven Lead closes
// the lifecycle — which is the point of the realignment.

builder.Build().Run();

static string UserSecretsPath(string id)
{
    var root = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "UserSecrets")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".microsoft", "usersecrets");
    return Path.Combine(root, id, "secrets.json");
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

