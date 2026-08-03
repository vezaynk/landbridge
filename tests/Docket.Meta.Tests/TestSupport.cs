using Docket.Meta;
using Docket.Meta.Data;
using Docket.Meta.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Docket.Meta.Tests;

/// <summary>
/// Deterministic, zero-Docker wiring for the saga tests (design note §7): an
/// in-memory store, the two fakes, a fake clock, and a stub HTTP probe that reports
/// the plane "up" on first poll — so <see cref="InstanceProvisioner"/> runs its real
/// logic end to end with no external dependency (hence 0-skip).
/// </summary>
public sealed class SagaHarness : IDisposable
{
    public MetaDbContext Db { get; }
    public FakeSubstrate Substrate { get; } = new();
    public FakeCaddyAdmin Caddy { get; } = new();
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));
    public MetaOptions Options { get; } = new() { Domain = "example.com" };
    public SecretGenerator Secrets { get; }
    public InstanceProvisioner Provisioner { get; }
    public InstanceCreator Creator { get; }
    public PlacementService Placement { get; }

    public SagaHarness(SecretGenerator? secrets = null)
    {
        Db = new MetaDbContext(new DbContextOptionsBuilder<MetaDbContext>()
            .UseInMemoryDatabase("meta-" + Guid.NewGuid()).Options);
        Secrets = secrets ?? new SecretGenerator();
        Placement = new PlacementService(Db);
        Creator = new InstanceCreator(Db, Placement, Secrets, Clock);

        var recipe = new InstanceRecipe(Options);
        var probe = new InstanceHealthProbe(new HttpClient(new AlwaysOkHandler()));
        Provisioner = new InstanceProvisioner(
            Db, new FakeSubstrateFactory(Substrate), Caddy, recipe, probe, Options, Clock,
            NullLogger<InstanceProvisioner>.Instance);
    }

    /// <summary>Registers a local host so placement + create have somewhere to land.</summary>
    public async Task<HostRow> AddHostAsync(string name = "local")
    {
        var host = new HostRow
        {
            Id = Guid.NewGuid(),
            Name = name,
            EndpointUri = "unix:///var/run/docker.sock",
            EndpointKind = HostEndpointKind.UnixSocket,
            PublishedHost = "127.0.0.1",
            CreatedAt = Clock.GetUtcNow(),
        };
        Db.Hosts.Add(host);
        await Db.SaveChangesAsync();
        return host;
    }

    public void Dispose() => Db.Dispose();

    private sealed class AlwaysOkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}

/// <summary>A <see cref="SecretGenerator"/> with fixed outputs so tests assert exact injected values.</summary>
public sealed class DeterministicSecrets : SecretGenerator
{
    public const string FixedPassphrase = "passphrase-plaintext-shown-once";
    public const string FixedSecret = "fixedsecret";
    public override string NewPassphrase() => FixedPassphrase;
    public override string NewSecret() => FixedSecret;
}
