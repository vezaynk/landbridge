using System.Diagnostics;
using Docket.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Docket.ControlPlane.Tests;

/// <summary>
/// An ephemeral Postgres cluster for the test session — initdb + pg_ctl into a
/// temp dir, torn down at the end. No shell scripts: every step is a spawned
/// process via ArgumentList (repo convention). Set DOCKET_TEST_PG to a
/// connection string to point at an existing server instead and skip the
/// managed cluster entirely.
///
/// Skips (rather than fails) the whole collection when neither a connection
/// string nor the pg binaries are available, so a checkout without Postgres
/// still builds and runs the pure-logic suites.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private string? _dataDir;
    private string? _socketDir;
    public string ConnectionString { get; private set; } = "";
    public bool Available { get; private set; }
    public string? SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("DOCKET_TEST_PG");
        if (!string.IsNullOrWhiteSpace(external))
        {
            ConnectionString = external;
            Available = true;
            await MigrateAsync();
            return;
        }

        var initdb = Which("initdb");
        var pgCtl = Which("pg_ctl");
        if (initdb is null || pgCtl is null)
        {
            SkipReason = "no DOCKET_TEST_PG and initdb/pg_ctl not on PATH";
            return;
        }

        _dataDir = Directory.CreateTempSubdirectory("docket-pg-").FullName;
        _socketDir = Directory.CreateTempSubdirectory("docket-sock-").FullName;
        var port = FreePort();

        await Run(initdb, ["-D", _dataDir, "-U", "docket", "--auth=trust", "-E", "UTF8"]);
        await Run(pgCtl,
        [
            "-D", _dataDir, "-w", "start",
            "-o", $"-p {port} -k {_socketDir} -c listen_addresses=127.0.0.1",
        ]);

        ConnectionString =
            $"Host=127.0.0.1;Port={port};Username=docket;Database=postgres;Include Error Detail=true";
        Available = true;
        await MigrateAsync();
    }

    public DocketDbContext NewContext() =>
        new(DocketDbContext.BuildOptions(ConnectionString));

    /// <summary>
    /// Clears all rows between tests. Dispatch is machine-driven and spans
    /// every Team (spec §6), so a leftover submitted task would let one test's
    /// dispatcher claim another test's work — reset gives each test a clean
    /// resource pool. The collection runs serially, so truncation is safe.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var db = NewContext();
        // relay_grants and team_forward_usage belong here for the same reason as every other
        // table: both are keyed per Team and COUNTED per Team (§9 check 10's forward rate
        // limit and its byte tally), so a row surviving a reset silently changes what a later
        // test measures — and a class sharing one static TeamId would inherit it.
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE tasks, worker_instances, registered_services, task_events, credentials, machines, lead_events, lead_machine_bindings, preview_mappings, relay_grants, team_forward_usage, task_usage RESTART IDENTITY CASCADE");
    }

    private async Task MigrateAsync()
    {
        // Applies the checked-in InitialSchema migration, so the tests validate
        // the migration produces a working schema (not just the EF model).
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_dataDir is not null && Which("pg_ctl") is { } pgCtl)
        {
            try { await Run(pgCtl, ["-D", _dataDir, "-w", "-m", "immediate", "stop"]); }
            catch { /* best effort */ }
            TryDelete(_dataDir);
        }
        if (_socketDir is not null) TryDelete(_socketDir);
    }

    private static string? Which(string tool)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var candidate = Path.Combine(dir, tool);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task Run(string file, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(file)} exited {p.ExitCode}: {await stderr}");
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
