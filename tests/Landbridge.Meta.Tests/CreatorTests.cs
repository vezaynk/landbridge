using Landbridge.Meta.Data;
using Landbridge.Meta.Provisioning;
using Microsoft.EntityFrameworkCore;

namespace Landbridge.Meta.Tests;

/// <summary>
/// Create-time behavior (design note §2/§5): validation, placement, port allocation,
/// and the credential-bootstrap invariant that the passphrase is returned once and
/// only its hash is persisted — all before any Docker side effect exists.
/// </summary>
public class CreatorTests
{
    [Fact]
    public async Task Rejects_invalid_name()
    {
        using var h = new SagaHarness();
        await h.AddHostAsync();
        await Assert.ThrowsAsync<InstanceCreateException>(() =>
            h.Creator.CreateAsync(new CreateInstanceRequest("Bad_Name!", null, "latest", null), default));
    }

    [Fact]
    public async Task Rejects_duplicate_live_name()
    {
        using var h = new SagaHarness();
        await h.AddHostAsync();
        await h.Creator.CreateAsync(new CreateInstanceRequest("acme", null, "latest", null), default);
        await Assert.ThrowsAsync<InstanceCreateException>(() =>
            h.Creator.CreateAsync(new CreateInstanceRequest("acme", null, "latest", null), default));
    }

    [Fact]
    public async Task Requires_a_registered_host()
    {
        using var h = new SagaHarness();
        var ex = await Assert.ThrowsAsync<InstanceCreateException>(() =>
            h.Creator.CreateAsync(new CreateInstanceRequest("acme", null, "latest", null), default));
        Assert.Contains("host", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Allocates_two_distinct_ports_and_persists_provisioning_row()
    {
        using var h = new SagaHarness();
        await h.AddHostAsync();
        var result = await h.Creator.CreateAsync(new CreateInstanceRequest("acme", "Acme", "v1", null), default);

        var row = await h.Db.Instances.SingleAsync(i => i.Id == result.Id);
        Assert.Equal(InstanceState.Provisioning, row.State);
        Assert.NotNull(row.McpPublishedPort);
        Assert.NotNull(row.RelayPublishedPort);
        Assert.NotEqual(row.McpPublishedPort, row.RelayPublishedPort);
        Assert.Equal("v1", row.ImageTag);
        Assert.Equal("Acme", row.AccountLabel);
    }

    [Fact]
    public async Task Second_instance_gets_non_overlapping_ports()
    {
        using var h = new SagaHarness();
        await h.AddHostAsync();
        var a = await h.Creator.CreateAsync(new CreateInstanceRequest("a", null, "latest", null), default);
        var b = await h.Creator.CreateAsync(new CreateInstanceRequest("b", null, "latest", null), default);

        var ra = await h.Db.Instances.SingleAsync(i => i.Id == a.Id);
        var rb = await h.Db.Instances.SingleAsync(i => i.Id == b.Id);
        var ports = new[] { ra.McpPublishedPort, ra.RelayPublishedPort, rb.McpPublishedPort, rb.RelayPublishedPort };
        Assert.Equal(4, ports.Distinct().Count());
    }

    [Fact]
    public async Task Returns_passphrase_plaintext_but_persists_only_its_hash()
    {
        using var h = new SagaHarness(new DeterministicSecrets());
        await h.AddHostAsync();
        var result = await h.Creator.CreateAsync(new CreateInstanceRequest("acme", null, "latest", null), default);

        var row = await h.Db.Instances.SingleAsync(i => i.Id == result.Id);
        // The plaintext is what the shown-once page renders …
        Assert.Equal(DeterministicSecrets.FixedPassphrase, result.Passphrase);
        // … and the row holds only the PBKDF2 hash, never the plaintext.
        var v = new Landbridge.Meta.Auth.MetaOperatorVerifier(row.PassphraseHash);
        Assert.True(v.Verify(DeterministicSecrets.FixedPassphrase));
        Assert.NotEqual(result.Passphrase, row.PassphraseHash);
    }

    [Fact]
    public async Task Placement_picks_least_loaded_host()
    {
        using var h = new SagaHarness();
        var busy = await h.AddHostAsync("busy");
        var quiet = await h.AddHostAsync("quiet");
        // Load "busy" with one instance.
        await h.Creator.CreateAsync(new CreateInstanceRequest("x", null, "latest", busy.Id), default);

        var picked = await h.Placement.LeastLoadedHostAsync(default);
        Assert.Equal(quiet.Id, picked!.Id);
    }

    // ── image tag resolution ─────────────────────────────────────────────────
    //
    // The tag is the one create input with no safe fallback. It used to default to
    // "latest" — hardcoded here, ignoring Meta:DefaultImageTag entirely — and nothing
    // publishes "latest", so an omitted tag pinned an image that could never be pulled
    // and surfaced as a confusing registry error one saga step later.

    [Fact]
    public async Task An_omitted_tag_falls_back_to_the_configured_default()
    {
        using var h = new SagaHarness();
        h.Options.DefaultImageTag = "sha-abc123def456";
        await h.AddHostAsync();

        var result = await h.Creator.CreateAsync(new CreateInstanceRequest("acme", null, "", null), default);

        var row = await h.Db.Instances.SingleAsync(i => i.Id == result.Id);
        Assert.Equal("sha-abc123def456", row.ImageTag);
    }

    [Fact]
    public async Task An_explicit_tag_beats_the_configured_default()
    {
        using var h = new SagaHarness();
        h.Options.DefaultImageTag = "sha-abc123def456";
        await h.AddHostAsync();

        var result = await h.Creator.CreateAsync(new CreateInstanceRequest("acme", null, "v0.4.0", null), default);

        var row = await h.Db.Instances.SingleAsync(i => i.Id == result.Id);
        Assert.Equal("v0.4.0", row.ImageTag);
    }

    [Fact]
    public async Task No_tag_and_no_default_is_rejected_with_guidance()
    {
        using var h = new SagaHarness();          // Options.DefaultImageTag is "" by default
        await h.AddHostAsync();

        var ex = await Assert.ThrowsAsync<InstanceCreateException>(() =>
            h.Creator.CreateAsync(new CreateInstanceRequest("acme", null, "  ", null), default));

        // The operator needs to learn where tags come from, not just that one is missing.
        Assert.Contains("Meta:DefaultImageTag", ex.Message);
        Assert.Contains("publish-images", ex.Message);
        Assert.Contains("sha-", ex.Message);
        Assert.Contains("latest", ex.Message);
        Assert.Contains("docs/META.md", ex.Message);
    }

    [Fact]
    public async Task A_rejected_create_allocates_nothing()
    {
        using var h = new SagaHarness();
        await h.AddHostAsync();

        await Assert.ThrowsAsync<InstanceCreateException>(() =>
            h.Creator.CreateAsync(new CreateInstanceRequest("acme", null, null!, null), default));

        // Validated before placement and port allocation, so a doomed create leaves no
        // row and burns no port from the host's window.
        Assert.Empty(h.Db.Instances);
    }

    [Fact]
    public async Task A_whitespace_padded_default_is_trimmed()
    {
        using var h = new SagaHarness();
        h.Options.DefaultImageTag = "  v0.4.0  ";
        await h.AddHostAsync();

        var result = await h.Creator.CreateAsync(new CreateInstanceRequest("acme", null, "", null), default);

        var row = await h.Db.Instances.SingleAsync(i => i.Id == result.Id);
        Assert.Equal("v0.4.0", row.ImageTag);
    }
}
