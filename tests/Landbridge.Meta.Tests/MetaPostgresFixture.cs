using System.Diagnostics;
using Landbridge.Meta.Data;
using Landbridge.Meta.Secrets;
using Microsoft.EntityFrameworkCore;

namespace Landbridge.Meta.Tests;

/// <summary>
/// An ephemeral Postgres for the migration test, mirroring the plane's fixture: use
/// <c>LANDBRIDGE_TEST_PG</c> when set (CI), else spin a local cluster via initdb/pg_ctl
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
        var external = Environment.GetEnvironmentVariable("LANDBRIDGE_TEST_PG");
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
            SkipReason = "no LANDBRIDGE_TEST_PG and initdb/pg_ctl not on PATH";
            return;
        }

        _dataDir = Directory.CreateTempSubdirectory("landbridge-meta-pg-").FullName;
        _socketDir = Directory.CreateTempSubdirectory("landbridge-meta-sock-").FullName;
        var port = FreePort();

        await Run(initdb, ["-D", _dataDir, "-U", "landbridge", "--auth=trust", "-E", "UTF8"]);
        await Run(pgCtl, ["-D", _dataDir, "-w", "start", "-o", $"-p {port} -k {_socketDir} -c listen_addresses=127.0.0.1"]);

        ConnectionString =
            $"Host=127.0.0.1;Port={port};Username=landbridge;Database=landbridge_meta;Include Error Detail=true";
        Available = true;
        await MigrateAsync();
    }

    /// <summary>The fixture's base64 key — exposed so a rotation test can retire it.</summary>
    public string ProtectorKey { get; } = MetaSecretProtector.NewKey();

    /// <summary>
    /// The fixture's default protector. Held on the fixture (not per-context) so every
    /// context in a test reads back what another wrote; a test that needs a DIFFERENT
    /// key passes one to <see cref="NewContext(MetaSecretProtector)"/>.
    /// </summary>
    public MetaSecretProtector Protector => _protector ??= new MetaSecretProtector([ProtectorKey]);
    private MetaSecretProtector? _protector;

    public MetaDbContext NewContext() => NewContext(Protector);

    /// <summary>A context over the same database but a caller-chosen key — the wrong-key path.</summary>
    public MetaDbContext NewContext(MetaSecretProtector protector) =>
        new(MetaDbContext.BuildOptions(ConnectionString), protector);

    /// <summary>
    /// Reads a column straight from Postgres, bypassing EF and its value converters —
    /// the only honest way to assert what actually landed on disk.
    /// </summary>
    public async Task<string?> RawColumnAsync(string table, string column, Guid id)
    {
        await using var conn = new Npgsql.NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM {table} WHERE id = @id";
        cmd.Parameters.AddWithValue("id", id);
        var value = await cmd.ExecuteScalarAsync();
        return value == DBNull.Value ? null : (string?)value;
    }

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
