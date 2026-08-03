using Docket.Meta.Data;
using Docket.Meta.Provisioning;
using Microsoft.EntityFrameworkCore;

namespace Docket.Meta.Tests;

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
        // … and the row holds only its SHA-256 hash, never the plaintext.
        Assert.Equal(SecretGenerator.Hash(DeterministicSecrets.FixedPassphrase), row.PassphraseHash);
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
}
