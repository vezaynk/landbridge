// Docket's local development inner loop. This is a dev-time orchestrator only —
// NOT a production deployment path. In production docketd runs standalone on
// each machine (§10, restart=reboot); nothing here couples the runtime to
// Aspire. What one `dotnet run` here buys you: a managed Postgres and the
// MCP/control-plane host wired to it, plus the Aspire dashboard capturing the
// host's OpenTelemetry traces, logs, and metrics — the cross-process insight §1
// is built around.
//
// Scope note: this first cut orchestrates just the plane (Postgres + MCP host).
// Bringing a docketd runner and a worker into the loop — where the end-to-end
// Lead → plane → runner → worker traces light up — is a deliberate follow-up: a
// runner has to enroll, which means minting a bootstrap token into the dev loop
// first.
var builder = DistributedApplication.CreateBuilder(args);

// A managed Postgres container with a persistent data volume, so the schema and
// data survive across `dotnet run`s. (The ephemeral initdb cluster the tests
// spin up is a separate, test-only concern — see PostgresFixture.) The database
// is named `docket` to match the connection-string key the MCP host already
// reads via GetConnectionString("Docket").
var docketDb = builder.AddPostgres("postgres")
    .WithDataVolume()
    .AddDatabase("docket");

// The MCP + control-plane host. WithReference injects Postgres as
// ConnectionStrings__docket (the host reads it through GetConnectionString);
// WaitFor holds the host's startup until Postgres reports healthy; and
// MigrateOnStartup tells the host to apply the checked-in EF migration against
// the fresh container — a dev-loop-only signal that production and the test
// fixtures never set.
builder.AddProject<Projects.Docket_Mcp>("mcp")
    .WithReference(docketDb)
    .WaitFor(docketDb)
    .WithEnvironment("Docket__MigrateOnStartup", "true");

builder.Build().Run();
