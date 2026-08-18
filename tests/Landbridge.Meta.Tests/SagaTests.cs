using Landbridge.Meta.Data;
using Landbridge.Meta.Provisioning;
using Microsoft.EntityFrameworkCore;

namespace Landbridge.Meta.Tests;

/// <summary>
/// The provisioning saga (design note §2): happy path, credential injection,
/// idempotency, crash-and-resume, failure recording, destroy/rollback, suspend/
/// resume, and image upgrade — all against the in-memory substrate (0-skip).
/// </summary>
public class SagaTests
{
    private static async Task<InstanceRow> CreateAsync(SagaHarness h, string name = "acme", string tag = "v1")
    {
        await h.AddHostAsync();
        var r = await h.Creator.CreateAsync(new CreateInstanceRequest(name, null, tag, null), default);
        return await h.Db.Instances.Include(i => i.Host).SingleAsync(i => i.Id == r.Id);
    }

    [Fact]
    public async Task Happy_path_reaches_ready_with_all_objects()
    {
        using var h = new SagaHarness();
        var i = await CreateAsync(h);

        await h.Provisioner.ProvisionAsync(i.Id, default);

        var row = await h.Db.Instances.Include(x => x.Steps).SingleAsync(x => x.Id == i.Id);
        Assert.Equal(InstanceState.Ready, row.State);
        Assert.All(row.Steps, s => Assert.Equal(StepStatus.Done, s.Status));
        Assert.Equal(8, row.Steps.Count);

        Assert.Contains("landbridge-acme-net", h.Substrate.Networks.Keys);
        Assert.Contains("landbridge-acme-pgdata", h.Substrate.Volumes);
        Assert.Contains("landbridge-acme-pg", h.Substrate.Containers.Keys);
        Assert.Contains("landbridge-acme-mcp", h.Substrate.Containers.Keys);
        Assert.Contains("landbridge-acme-relay", h.Substrate.Containers.Keys);

        // Two public routes: mcp + relay (deviation #2).
        Assert.True(h.Caddy.Routes.ContainsKey("landbridge-acme-mcp"));
        Assert.True(h.Caddy.Routes.ContainsKey("landbridge-acme-relay"));
        Assert.Equal("acme.landbridge.example.com", h.Caddy.Routes["landbridge-acme-mcp"].Host);
        Assert.Equal("relay-acme.landbridge.example.com", h.Caddy.Routes["landbridge-acme-relay"].Host);
        Assert.Equal("https://acme.landbridge.example.com", row.PublicUrl);
    }

    [Fact]
    public async Task Injects_credentials_and_never_the_dev_gates()
    {
        using var h = new SagaHarness();
        var i = await CreateAsync(h);
        await h.Provisioner.ProvisionAsync(i.Id, default);

        var mcp = h.Substrate.Containers["landbridge-acme-mcp"].Spec.Env;
        Assert.Equal(i.PassphraseHash, mcp["Landbridge__Operator__PassphraseHash"]);
        Assert.Equal("true", mcp["Landbridge__MigrateOnStartup"]);
        Assert.Equal(i.RelayBearer, mcp["Landbridge__RelayValidation__Bearer"]);
        Assert.Contains("Password=" + i.DbPassword, mcp["ConnectionStrings__Landbridge"]);
        Assert.Contains("Host=landbridge-acme-pg", mcp["ConnectionStrings__Landbridge"]);
        Assert.Equal("https://relay-acme.landbridge.example.com", mcp["Landbridge__RelayUrl"]);
        // The dev-only gates are never set on a meta-provisioned instance (ruling #1 area).
        Assert.False(mcp.ContainsKey("Landbridge__DevSeed__TokenFile"));
        Assert.False(mcp.ContainsKey("Landbridge__DevSeed__TokenDir"));
        Assert.False(mcp.ContainsKey("Landbridge__Oauth__AllowInsecureClientMetadata"));
        // No signing key is injected (ruling #1: opaque tokens, no consumer).
        Assert.DoesNotContain(mcp.Keys, k => k.Contains("SigningKey", StringComparison.OrdinalIgnoreCase));

        var relay = h.Substrate.Containers["landbridge-acme-relay"].Spec.Env;
        Assert.Equal(i.RelayBearer, relay["Relay__ControlPlane__Bearer"]);
        Assert.Equal("http://landbridge-acme-mcp:8080", relay["Relay__ControlPlane__Url"]);
    }

    [Fact]
    public async Task Is_idempotent_on_rerun()
    {
        using var h = new SagaHarness();
        var i = await CreateAsync(h);
        var ops = CountOps(h);

        await h.Provisioner.ProvisionAsync(i.Id, default);
        await h.Provisioner.ProvisionAsync(i.Id, default); // re-run: every step already Done

        Assert.Equal(1, ops["net:landbridge-acme-net"]);
        Assert.Equal(1, ops["run:landbridge-acme-mcp"]);
        Assert.Equal(1, ops["run:landbridge-acme-relay"]);
        Assert.Single(h.Substrate.Networks);
        Assert.Equal(3, h.Substrate.Containers.Count);
    }

    [Fact]
    public async Task Resumes_from_failed_step_without_repeating_earlier_ones()
    {
        using var h = new SagaHarness();
        var i = await CreateAsync(h);
        var ops = CountOps(h);
        var fail = true;
        var prior = h.Substrate.BeforeOp!;
        h.Substrate.BeforeOp = async op =>
        {
            await prior(op);
            if (fail && op == "run:landbridge-acme-relay")
                throw new InvalidOperationException("boom");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Provisioner.ProvisionAsync(i.Id, default));
        var failed = await h.Db.Instances.SingleAsync(x => x.Id == i.Id);
        Assert.Equal(InstanceState.Failed, failed.State);
        Assert.Equal(ProvisionStep.StartRelay, failed.FailedStep);

        fail = false;
        await h.Provisioner.ProvisionAsync(i.Id, default);

        var ready = await h.Db.Instances.SingleAsync(x => x.Id == i.Id);
        Assert.Equal(InstanceState.Ready, ready.State);
        Assert.Null(ready.FailedStep);
        // Earlier steps ran once; only the failed relay step retried.
        Assert.Equal(1, ops["net:landbridge-acme-net"]);
        Assert.Equal(1, ops["run:landbridge-acme-mcp"]);
        Assert.Equal(2, ops["run:landbridge-acme-relay"]);
    }

    [Fact]
    public async Task Destroy_removes_everything_and_tombstones()
    {
        using var h = new SagaHarness();
        var i = await CreateAsync(h);
        await h.Provisioner.ProvisionAsync(i.Id, default);

        await h.Provisioner.DestroyAsync(i.Id, default);

        var row = await h.Db.Instances.SingleAsync(x => x.Id == i.Id);
        Assert.Equal(InstanceState.Destroyed, row.State);
        Assert.NotNull(row.DestroyedAt);
        Assert.Empty(h.Substrate.Containers);
        Assert.Empty(h.Substrate.Networks);
        Assert.Empty(h.Substrate.Volumes);
        Assert.Empty(h.Caddy.Routes);
        // Tombstone drops live secret material.
        Assert.Equal("", row.DbPassword);
        Assert.Equal("", row.RelayBearer);
    }

    [Fact]
    public async Task Destroy_completes_even_when_caddy_is_unreachable()
    {
        using var h = new SagaHarness();
        var i = await CreateAsync(h);
        await h.Provisioner.ProvisionAsync(i.Id, default);

        h.Caddy.Unreachable = true; // route removal will throw; teardown must not
        await h.Provisioner.DestroyAsync(i.Id, default);

        var row = await h.Db.Instances.SingleAsync(x => x.Id == i.Id);
        Assert.Equal(InstanceState.Destroyed, row.State);
        Assert.Empty(h.Substrate.Containers);
        Assert.Empty(h.Substrate.Volumes);
    }

    [Fact]
    public async Task Suspend_then_resume_keeps_volume_and_restores_routes()
    {
        using var h = new SagaHarness();
        var i = await CreateAsync(h);
        await h.Provisioner.ProvisionAsync(i.Id, default);

        await h.Provisioner.SuspendAsync(i.Id, default);
        var suspended = await h.Db.Instances.SingleAsync(x => x.Id == i.Id);
        Assert.Equal(InstanceState.Suspended, suspended.State);
        Assert.All(h.Substrate.Containers.Values, c => Assert.False(c.Running));
        Assert.Empty(h.Caddy.Routes);
        Assert.Contains("landbridge-acme-pgdata", h.Substrate.Volumes); // data retained

        await h.Provisioner.ResumeAsync(i.Id, default);
        var resumed = await h.Db.Instances.SingleAsync(x => x.Id == i.Id);
        Assert.Equal(InstanceState.Ready, resumed.State);
        Assert.All(h.Substrate.Containers.Values, c => Assert.True(c.Running));
        Assert.Equal(2, h.Caddy.Routes.Count);
    }

    [Fact]
    public async Task Upgrade_recreates_plane_containers_and_keeps_postgres()
    {
        using var h = new SagaHarness();
        var i = await CreateAsync(h, tag: "v1");
        await h.Provisioner.ProvisionAsync(i.Id, default);
        var ops = CountOps(h);

        await h.Provisioner.UpgradeAsync(i.Id, "v2", default);

        var row = await h.Db.Instances.SingleAsync(x => x.Id == i.Id);
        Assert.Equal("v2", row.ImageTag);
        Assert.Equal(InstanceState.Ready, row.State);
        // mcp + relay were removed and recreated on the new tag …
        Assert.Contains("container:landbridge-acme-mcp", h.Substrate.Removed);
        Assert.Contains("container:landbridge-acme-relay", h.Substrate.Removed);
        Assert.Equal("landbridge-mcp:v2", h.Substrate.Containers["landbridge-acme-mcp"].Spec.Image);
        // … Postgres and its volume were untouched.
        Assert.DoesNotContain("container:landbridge-acme-pg", h.Substrate.Removed);
        Assert.Contains("landbridge-acme-pgdata", h.Substrate.Volumes);
        Assert.False(ops.ContainsKey("rm:landbridge-acme-pg"));
    }

    /// <summary>Attaches a counting hook, preserving any existing one; returns the live counter.</summary>
    private static Dictionary<string, int> CountOps(SagaHarness h)
    {
        var counts = new Dictionary<string, int>();
        var prior = h.Substrate.BeforeOp;
        h.Substrate.BeforeOp = async op =>
        {
            counts[op] = counts.GetValueOrDefault(op) + 1;
            if (prior is not null) await prior(op);
        };
        return counts;
    }
}
