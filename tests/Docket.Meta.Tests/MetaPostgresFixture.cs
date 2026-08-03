using System.Diagnostics;
using Docket.Meta.Data;
using Microsoft.EntityFrameworkCore;

namespace Docket.Meta.Tests;

/// <summary>
/// An ephemeral Postgres for the migration test, mirroring the plane's fixture: use
/// <c>DOCKET_TEST_PG</c> when set (CI), else spin a local cluster via initdb/pg_ctl
/// (no shell — processes spawned via ArgumentList, repo convention). Skips (not
/// fails) when neither is available, so a checkout without Postgres still runs the
/// InMemory suites.
/// </summary>
public sealed class MetaPostgresFixture : IAsyncLifetime
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

        _dataDir = Directory.CreateTempSubdirectory("docket-meta-pg-").FullName;
        _socketDir = Directory.CreateTempSubdirectory("docket-meta-sock-").FullName;
        var port = FreePort();

        await Run(initdb, ["-D", _dataDir, "-U", "docket", "--auth=trust", "-E", "UTF8"]);
        await Run(pgCtl, ["-D", _dataDir, "-w", "start", "-o", $"-p {port} -k {_socketDir} -c listen_addresses=127.0.0.1"]);

        ConnectionString =
            $"Host=127.0.0.1;Port={port};Username=docket;Database=docket_meta;Include Error Detail=true";
        Available = true;
        await MigrateAsync();
    }

    public MetaDbContext NewContext() => new(MetaDbContext.BuildOptions(ConnectionString));

    private async Task MigrateAsync()
    {
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
        var psi = new ProcessStartInfo(file) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
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
public sealed class MetaPostgresCollection : ICollectionFixture<MetaPostgresFixture>
{
    public const string Name = "meta-postgres";
}
