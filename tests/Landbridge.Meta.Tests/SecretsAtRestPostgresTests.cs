using Landbridge.Meta.Data;
using Landbridge.Meta.Provisioning;
using Landbridge.Meta.Secrets;
using Microsoft.EntityFrameworkCore;

namespace Landbridge.Meta.Tests;

/// <summary>
/// What actually lands on disk (task #79). The in-memory suites prove the round-trip;
/// only a real Postgres can prove the ciphertext is what Postgres holds, so every
/// assertion here reads the column with raw SQL, bypassing EF and its converters —
/// the planted-value pattern. Skippable on the same terms as the migration tests.
/// </summary>
[Collection(MetaPostgresCollection.Name)]
public class SecretsAtRestPostgresTests(MetaPostgresFixture pg)
{
    private const string PlantedPassword = "planted-db-password-8f2a";
    private const string PlantedBearer = "planted-relay-bearer-4c1d";
    private const string PlantedTlsKey = "-----BEGIN PRIVATE KEY-----planted-9b3e-----END PRIVATE KEY-----";

    [SkippableFact]
    public async Task Retained_secrets_are_ciphertext_in_postgres()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        await ResetAsync(db);

        var host = NewHost(tlsKey: PlantedTlsKey);
        db.Hosts.Add(host);
        var instance = NewInstance("atrest", host.Id);
        db.Instances.Add(instance);
        await db.SaveChangesAsync();

        // The plaintext must not appear anywhere in the stored columns.
        var storedPassword = await pg.RawColumnAsync("instances", "db_password", instance.Id);
        var storedBearer = await pg.RawColumnAsync("instances", "relay_bearer", instance.Id);
        var storedTlsKey = await pg.RawColumnAsync("hosts", "tls_client_key_pem", host.Id);

        Assert.DoesNotContain(PlantedPassword, storedPassword);
        Assert.DoesNotContain(PlantedBearer, storedBearer);
        Assert.DoesNotContain("planted-9b3e", storedTlsKey);
        Assert.True(MetaSecretProtector.IsProtected(storedPassword), $"db_password not sealed: {storedPassword}");
        Assert.True(MetaSecretProtector.IsProtected(storedBearer), $"relay_bearer not sealed: {storedBearer}");
        Assert.True(MetaSecretProtector.IsProtected(storedTlsKey), $"tls_client_key_pem not sealed: {storedTlsKey}");

        // The passphrase HASH is deliberately NOT encrypted — it is already one-way.
        var storedHash = await pg.RawColumnAsync("instances", "passphrase_hash", instance.Id);
        Assert.Equal("hash-not-a-secret", storedHash);

        // And EF still hands back the real values.
        await using var read = pg.NewContext();
        var row = await read.Instances.Include(i => i.Host).SingleAsync(i => i.Id == instance.Id);
        Assert.Equal(PlantedPassword, row.DbPassword);
        Assert.Equal(PlantedBearer, row.RelayBearer);
        Assert.Equal(PlantedTlsKey, row.Host!.TlsClientKeyPem);
    }

    [SkippableFact]
    public async Task Public_tls_material_stays_readable()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        await ResetAsync(db);

        var host = NewHost(tlsKey: PlantedTlsKey);
        host.TlsCaPem = "-----BEGIN CERTIFICATE-----ca-----END CERTIFICATE-----";
        host.TlsClientCertPem = "-----BEGIN CERTIFICATE-----client-----END CERTIFICATE-----";
        db.Hosts.Add(host);
        await db.SaveChangesAsync();

        // Certificates are public material; leaving them legible lets an operator audit
        // which host a row points at without holding the encryption key.
        Assert.Equal(host.TlsCaPem, await pg.RawColumnAsync("hosts", "tls_ca_pem", host.Id));
        Assert.Equal(host.TlsClientCertPem, await pg.RawColumnAsync("hosts", "tls_client_cert_pem", host.Id));
    }

    [SkippableFact]
    public async Task Destroy_shreds_the_columns_on_disk()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        await ResetAsync(db);

        // The real destroy path, against the real store — not a hand-mimicked blanking.
        using var h = new SagaHarness(new DeterministicSecrets(), store: db);
        var host = await h.AddHostAsync("shred-local");
        var created = await h.Creator.CreateAsync(new CreateInstanceRequest("shredme", null, "latest", host.Id), default);
        await h.Provisioner.ProvisionAsync(created.Id, default);
        Assert.True(MetaSecretProtector.IsProtected(
            await pg.RawColumnAsync("instances", "db_password", created.Id)));

        await h.Provisioner.DestroyAsync(created.Id, default);

        // Blank, not "an encrypted empty string" — a shred should be visible as one.
        Assert.Equal("", await pg.RawColumnAsync("instances", "db_password", created.Id));
        Assert.Equal("", await pg.RawColumnAsync("instances", "relay_bearer", created.Id));
        Assert.Equal("", await pg.RawColumnAsync("instances", "passphrase_hash", created.Id));

        // And the tombstone is skipped by the rewrap sweep — nothing to re-seal.
        await using var sweep = pg.NewContext();
        Assert.Equal(0, await SecretRewrapSweeper.SweepAsync(sweep, default));
    }

    [SkippableFact]
    public async Task Sweep_seals_a_pre_encryption_plaintext_row()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        await ResetAsync(db);

        var host = NewHost();
        db.Hosts.Add(host);
        var instance = NewInstance("legacy", host.Id);
        db.Instances.Add(instance);
        await db.SaveChangesAsync();

        // Plant bare plaintext the way a pre-encryption meta would have written it.
        await WriteRawAsync("instances", "db_password", instance.Id, PlantedPassword);
        await WriteRawAsync("instances", "relay_bearer", instance.Id, PlantedBearer);
        Assert.False(MetaSecretProtector.IsProtected(
            await pg.RawColumnAsync("instances", "db_password", instance.Id)));

        // It reads through unchanged (no stranded instance) ...
        await using (var read = pg.NewContext())
        {
            var row = await read.Instances.SingleAsync(i => i.Id == instance.Id);
            Assert.Equal(PlantedPassword, row.DbPassword);
        }

        // ... and the sweep converts it in place.
        await using (var sweep = pg.NewContext())
        {
            Assert.Equal(1, await SecretRewrapSweeper.SweepAsync(sweep, default));
        }

        var sealedPassword = await pg.RawColumnAsync("instances", "db_password", instance.Id);
        Assert.True(MetaSecretProtector.IsProtected(sealedPassword));
        Assert.DoesNotContain(PlantedPassword, sealedPassword);
        Assert.True(pg.Protector.IsUnderPrimaryKey(sealedPassword));

        await using var after = pg.NewContext();
        var afterRow = await after.Instances.SingleAsync(i => i.Id == instance.Id);
        Assert.Equal(PlantedPassword, afterRow.DbPassword);
        Assert.Equal(PlantedBearer, afterRow.RelayBearer);
    }

    [SkippableFact]
    public async Task Rotation_rewraps_so_the_old_key_can_be_dropped()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        await ResetAsync(db);

        var host = NewHost();
        db.Hosts.Add(host);
        var instance = NewInstance("rotate", host.Id);
        db.Instances.Add(instance);
        await db.SaveChangesAsync();

        var oldFingerprint = pg.Protector.PrimaryFingerprint;
        Assert.True(pg.Protector.IsUnderPrimaryKey(
            await pg.RawColumnAsync("instances", "db_password", instance.Id)));

        // Rotate: new key primary, the fixture's key retired but still configured.
        var newKey = MetaSecretProtector.NewKey();
        var rotating = new MetaSecretProtector([newKey, pg.ProtectorKey]);
        await using (var sweep = pg.NewContext(rotating))
        {
            Assert.Equal(1, await SecretRewrapSweeper.SweepAsync(sweep, default));
        }

        var rewrapped = await pg.RawColumnAsync("instances", "db_password", instance.Id);
        Assert.True(rotating.IsUnderPrimaryKey(rewrapped));
        Assert.NotEqual(oldFingerprint, rotating.PrimaryFingerprint);

        // The retired key is now droppable: a protector holding ONLY the new key reads
        // the store. That is the property DataProtection's rolling key ring cannot give.
        await using var newOnly = pg.NewContext(new MetaSecretProtector([newKey]));
        var row = await newOnly.Instances.SingleAsync(i => i.Id == instance.Id);
        Assert.Equal(PlantedPassword, row.DbPassword);
        Assert.Equal(PlantedBearer, row.RelayBearer);
    }

    [SkippableFact]
    public async Task A_wrong_key_refuses_to_read_rather_than_returning_ciphertext()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        await ResetAsync(db);

        var host = NewHost();
        db.Hosts.Add(host);
        var instance = NewInstance("wrongkey", host.Id);
        db.Instances.Add(instance);
        await db.SaveChangesAsync();

        await using var stranger = pg.NewContext(new MetaSecretProtector([MetaSecretProtector.NewKey()]));
        var ex = await Assert.ThrowsAsync<MetaSecretKeyException>(
            () => stranger.Instances.SingleAsync(i => i.Id == instance.Id));

        Assert.Contains(pg.Protector.PrimaryFingerprint, ex.Message);
        Assert.DoesNotContain(PlantedPassword, ex.Message);
    }

    [SkippableFact]
    public async Task Rotation_also_rewraps_a_host_tls_key()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        await ResetAsync(db);

        var host = NewHost(tlsKey: PlantedTlsKey);
        db.Hosts.Add(host);
        await db.SaveChangesAsync();

        // A host key left under the retired key would keep that key required forever,
        // which would defeat the whole point of a completable rotation.
        var newKey = MetaSecretProtector.NewKey();
        var rotating = new MetaSecretProtector([newKey, pg.ProtectorKey]);
        await using (var sweep = pg.NewContext(rotating))
        {
            Assert.Equal(1, await SecretRewrapSweeper.SweepAsync(sweep, default));
        }

        Assert.True(rotating.IsUnderPrimaryKey(
            await pg.RawColumnAsync("hosts", "tls_client_key_pem", host.Id)));

        await using var newOnly = pg.NewContext(new MetaSecretProtector([newKey]));
        var row = await newOnly.Hosts.SingleAsync(h => h.Id == host.Id);
        Assert.Equal(PlantedTlsKey, row.TlsClientKeyPem);
    }

    [SkippableFact]
    public async Task Saga_round_trip_seals_on_write_and_injects_plaintext_on_resume()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var store = pg.NewContext();
        await ResetAsync(store);

        // The saga against the REAL store (fakes only for Docker and the edge), so the
        // value converters actually run — the round-trip the in-memory suites cannot see.
        using var h = new SagaHarness(new DeterministicSecrets(), store: store);
        var host = await h.AddHostAsync("pg-local");
        var created = await h.Creator.CreateAsync(new CreateInstanceRequest("sagaenc", null, "latest", host.Id), default);
        await h.Provisioner.ProvisionAsync(created.Id, default);

        // Sealed on disk ...
        var stored = await pg.RawColumnAsync("instances", "db_password", created.Id);
        Assert.True(MetaSecretProtector.IsProtected(stored), $"db_password not sealed: {stored}");
        Assert.DoesNotContain(DeterministicSecrets.FixedSecret, stored);

        // ... plaintext in the container.
        var mcp = h.Substrate.Containers[InstanceNaming.McpContainer("sagaenc")].Spec.Env;
        Assert.Contains("Password=" + DeterministicSecrets.FixedSecret, mcp["ConnectionStrings__Landbridge"]);

        // Resume reads the sealed columns back and rebuilds the containers from them —
        // the operation that breaks first if decryption is wrong.
        await h.Provisioner.SuspendAsync(created.Id, default);
        await h.Provisioner.ResumeAsync(created.Id, default);

        await using var read = pg.NewContext();
        var row = await read.Instances.SingleAsync(i => i.Id == created.Id);
        Assert.Equal(InstanceState.Ready, row.State);
        Assert.Equal(DeterministicSecrets.FixedSecret, row.DbPassword);
        Assert.Equal(DeterministicSecrets.FixedSecret, row.RelayBearer);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task WriteRawAsync(string table, string column, Guid id, string value)
    {
        await using var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE {table} SET {column} = @v WHERE id = @id";
        cmd.Parameters.AddWithValue("v", value);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    private static HostRow NewHost(string? tlsKey = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "host-" + Guid.NewGuid().ToString("N")[..8],
        EndpointUri = tlsKey is null ? "unix:///var/run/docker.sock" : "https://docker.example:2376",
        EndpointKind = tlsKey is null ? HostEndpointKind.UnixSocket : HostEndpointKind.TcpMtls,
        TlsClientKeyPem = tlsKey,
        PublishedHost = "127.0.0.1",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static InstanceRow NewInstance(string name, Guid hostId) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        HostId = hostId,
        ImageTag = "latest",
        State = InstanceState.Ready,
        PassphraseHash = "hash-not-a-secret",
        DbPassword = PlantedPassword,
        RelayBearer = PlantedBearer,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task ResetAsync(MetaDbContext db) =>
        await db.Database.ExecuteSqlRawAsync("TRUNCATE instances, instance_steps, hosts RESTART IDENTITY CASCADE");
}
