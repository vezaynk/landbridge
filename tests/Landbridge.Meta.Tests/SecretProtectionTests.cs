using Docket.Meta.Data;
using Docket.Meta.Provisioning;
using Docket.Meta.Secrets;
using Microsoft.EntityFrameworkCore;

namespace Docket.Meta.Tests;

/// <summary>
/// At-rest protection for the secrets meta RETAINS (task #79): the instance database
/// password, the relay shared bearer, and a remote host's mTLS client key. These cover
/// the mechanism itself, its fail-closed behaviour, the rotation window, and the model
/// wiring that makes encryption structural.
///
/// <para><b>Why the crypto round-trip is not tested here.</b> The EF InMemory provider
/// stores CLR values and does not run value converters, so on this store encryption is
/// a no-op — an in-memory assertion about ciphertext would pass vacuously. The saga
/// tests below therefore claim only that the saga still injects plaintext into
/// containers; everything about what reaches the database lives in
/// <see cref="SecretsAtRestPostgresTests"/>, against a real Postgres.</para>
/// </summary>
public class SecretProtectionTests
{
    private const string Purpose = "instance.db_password";

    private static MetaSecretProtector One(out string key)
    {
        key = MetaSecretProtector.NewKey();
        return new MetaSecretProtector([key]);
    }

    [Fact]
    public void Round_trips_a_value()
    {
        var p = One(out _);
        var sealed_ = p.Protect("s3cret", Purpose);

        Assert.NotEqual("s3cret", sealed_);
        Assert.DoesNotContain("s3cret", sealed_);
        Assert.True(MetaSecretProtector.IsProtected(sealed_));
        Assert.Equal("s3cret", p.Unprotect(sealed_, Purpose));
    }

    [Fact]
    public void Same_value_seals_differently_each_time()
    {
        var p = One(out _);

        // A fresh nonce per call: equal plaintexts must not produce equal ciphertexts,
        // or the store would leak which instances share a secret.
        Assert.NotEqual(p.Protect("same", Purpose), p.Protect("same", Purpose));
    }

    [Fact]
    public void Empty_stays_empty_so_a_shred_is_visible()
    {
        var p = One(out _);

        // Destroy blanks these columns; an encrypted empty string would make a
        // tombstone look like it still held a secret.
        Assert.Equal("", p.Protect("", Purpose));
        Assert.Equal("", p.Unprotect("", Purpose));
    }

    [Fact]
    public void No_key_configured_is_fail_closed()
    {
        var ex = Assert.Throws<MetaSecretKeyException>(() => new MetaSecretProtector([]));

        // The message has to tell an operator what to do — this is the error they meet
        // on a fresh deployment.
        Assert.Contains(MetaSecretProtector.KeysConfigKey, ex.Message);
        Assert.Contains("openssl rand -base64 32", ex.Message);
    }

    [Theory]
    [InlineData("not-base64!!")]
    [InlineData("c2hvcnQ=")] // valid base64, wrong length
    public void Malformed_key_is_rejected_with_guidance(string badKey)
    {
        var ex = Assert.Throws<MetaSecretKeyException>(() => new MetaSecretProtector([badKey]));
        Assert.Contains("openssl rand -base64 32", ex.Message);
    }

    [Fact]
    public void Wrong_key_fails_closed_and_names_the_key_it_wants()
    {
        var a = One(out _);
        var sealed_ = a.Protect("s3cret", Purpose);
        var b = One(out _); // a different key entirely

        var ex = Assert.Throws<MetaSecretKeyException>(() => b.Unprotect(sealed_, Purpose));
        Assert.Contains(a.PrimaryFingerprint, ex.Message);
        // Never a silent fallback to the stored bytes.
        Assert.DoesNotContain("s3cret", ex.Message);
    }

    [Fact]
    public void Retired_key_still_decrypts_while_new_values_use_the_primary()
    {
        var old = One(out var oldKey);
        var sealedUnderOld = old.Protect("s3cret", Purpose);

        // The rotation window: new key first (primary), retired key kept for reads.
        var newKey = MetaSecretProtector.NewKey();
        var rotating = new MetaSecretProtector([newKey, oldKey]);

        Assert.Equal("s3cret", rotating.Unprotect(sealedUnderOld, Purpose));
        Assert.False(rotating.IsUnderPrimaryKey(sealedUnderOld));

        var resealed = rotating.Protect("s3cret", Purpose);
        Assert.True(rotating.IsUnderPrimaryKey(resealed));

        // And once rewrapped, the old key can be dropped — that is the point of the
        // whole scheme: a rotation that can actually be finished.
        var newOnly = new MetaSecretProtector([newKey]);
        Assert.Equal("s3cret", newOnly.Unprotect(resealed, Purpose));
        Assert.Throws<MetaSecretKeyException>(() => newOnly.Unprotect(sealedUnderOld, Purpose));
    }

    [Fact]
    public void A_value_cannot_be_replayed_into_another_column()
    {
        var p = One(out _);
        var bearer = p.Protect("s3cret", "instance.relay_bearer");

        // The purpose is bound as associated data, so moving the ciphertext to the
        // db_password column fails authentication rather than decrypting.
        Assert.Throws<MetaSecretKeyException>(() => p.Unprotect(bearer, "instance.db_password"));
    }

    [Fact]
    public void Tampering_with_the_ciphertext_fails_closed()
    {
        var p = One(out _);
        var sealed_ = p.Protect("s3cret", Purpose);

        // Flip the last payload character — GCM authentication must reject it.
        var tampered = sealed_[..^1] + (sealed_[^1] == 'A' ? 'B' : 'A');
        Assert.Throws<MetaSecretKeyException>(() => p.Unprotect(tampered, Purpose));
    }

    [Fact]
    public void Pre_encryption_plaintext_reads_through_unchanged()
    {
        var p = One(out _);

        // Rows written before this landed hold bare plaintext. They must stay readable
        // (the sweep seals them) rather than stranding an instance's resume.
        Assert.Equal("legacy-plaintext", p.Unprotect("legacy-plaintext", Purpose));
        Assert.False(MetaSecretProtector.IsProtected("legacy-plaintext"));
    }

    // ── model wiring ─────────────────────────────────────────────────────────

    [Fact]
    public void Every_retained_secret_column_carries_a_converter()
    {
        using var db = SagaHarness.NewInMemoryDb("meta-model-" + Guid.NewGuid(), SagaHarness.NewProtector());

        // The guarantee is structural: encryption is a property of the MODEL, so no
        // future write path can bypass it. If a new retained secret is added without a
        // converter, this fails.
        AssertSealed(db, typeof(InstanceRow), nameof(InstanceRow.DbPassword));
        AssertSealed(db, typeof(InstanceRow), nameof(InstanceRow.RelayBearer));
        AssertSealed(db, typeof(HostRow), nameof(HostRow.TlsClientKeyPem));

        // The passphrase hash is one-way already, and the public certificates are not
        // secrets — deliberately left unconverted.
        AssertPlain(db, typeof(InstanceRow), nameof(InstanceRow.PassphraseHash));
        AssertPlain(db, typeof(HostRow), nameof(HostRow.TlsCaPem));
        AssertPlain(db, typeof(HostRow), nameof(HostRow.TlsClientCertPem));
    }

    [Fact]
    public void The_model_cache_is_keyed_on_the_encryption_key()
    {
        // EF caches a compiled model per context type. The converters close over the
        // protector, so without MetaModelCacheKeyFactory a second context would inherit
        // the FIRST one's key and silently seal under the wrong one.
        using var a = SagaHarness.NewInMemoryDb("meta-cache-a", SagaHarness.NewProtector());
        using var b = SagaHarness.NewInMemoryDb("meta-cache-b", SagaHarness.NewProtector());

        Assert.NotSame(Converter(a, typeof(InstanceRow), nameof(InstanceRow.DbPassword)),
                       Converter(b, typeof(InstanceRow), nameof(InstanceRow.DbPassword)));
    }

    // ── saga behaviour (in-memory: saga logic only, NOT the crypto) ───────────

    [Fact]
    public async Task Destroy_shreds_the_retained_secrets()
    {
        using var h = new SagaHarness(new DeterministicSecrets());
        var host = await h.AddHostAsync();
        var created = await h.Creator.CreateAsync(new CreateInstanceRequest("enc2", null, "latest", host.Id), default);
        await h.Provisioner.ProvisionAsync(created.Id, default);

        await h.Provisioner.DestroyAsync(created.Id, default);

        var row = await h.Db.Instances.FirstAsync(i => i.Id == created.Id);
        Assert.Equal(InstanceState.Destroyed, row.State);
        Assert.Equal("", row.DbPassword);
        Assert.Equal("", row.RelayBearer);
        Assert.Equal("", row.PassphraseHash);
    }

    [Fact]
    public async Task Provision_then_suspend_resume_still_injects_the_plaintext()
    {
        using var h = new SagaHarness(new DeterministicSecrets());
        var host = await h.AddHostAsync();
        var created = await h.Creator.CreateAsync(new CreateInstanceRequest("enc1", null, "latest", host.Id), default);
        await h.Provisioner.ProvisionAsync(created.Id, default);

        // What the container receives must be the plaintext, never the envelope.
        var mcp = h.Substrate.Containers[InstanceNaming.McpContainer("enc1")].Spec.Env;
        Assert.Equal(DeterministicSecrets.FixedSecret, mcp["Docket__RelayValidation__Bearer"]);
        Assert.Contains("Password=" + DeterministicSecrets.FixedSecret, mcp["ConnectionStrings__Docket"]);
        Assert.Equal(DeterministicSecrets.FixedSecret,
            h.Substrate.Containers[InstanceNaming.PgContainer("enc1")].Spec.Env["POSTGRES_PASSWORD"]);

        await h.Provisioner.SuspendAsync(created.Id, default);
        await h.Provisioner.ResumeAsync(created.Id, default);

        var row = await h.Db.Instances.FirstAsync(i => i.Id == created.Id);
        Assert.Equal(InstanceState.Ready, row.State);
        Assert.Equal(DeterministicSecrets.FixedSecret, row.RelayBearer);
    }

    private static void AssertSealed(MetaDbContext db, Type entity, string property) =>
        Assert.NotNull(Converter(db, entity, property));

    private static void AssertPlain(MetaDbContext db, Type entity, string property) =>
        Assert.Null(Converter(db, entity, property));

    private static object? Converter(MetaDbContext db, Type entity, string property) =>
        db.Model.FindEntityType(entity)!.FindProperty(property)!.GetValueConverter();
}
