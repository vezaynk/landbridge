using Docket.Meta;
using Docket.Meta.Data;
using Docket.Meta.Provisioning;
using Docket.Meta.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>
    /// The process-local "an operation is running here" marks the launcher and the
    /// reconciler share. Held on the harness so a test can drive a reconciler sweep with
    /// the same tracker the operations use.
    /// </summary>
    public OperationTracker Tracker { get; } = new();

    /// <summary>The key sealing this harness's retained secrets (task #79).</summary>
    public MetaSecretProtector Protector { get; }

    /// <param name="store">
    /// An external store to drive the saga against. Pass a Postgres-backed context to
    /// exercise the secret value converters for real: the EF InMemory provider keeps
    /// CLR values and ignores converters, so encryption is a no-op there (see
    /// <see cref="SecretsAtRestPostgresTests"/>).
    /// </param>
    /// <param name="stores">
    /// How to open a further context on the same store, for the multi-actor cases. Supplied
    /// with <paramref name="store"/> (e.g. the Postgres fixture's factory); for the harness's
    /// own in-memory store it is implicit.
    /// </param>
    public SagaHarness(
        SecretGenerator? secrets = null,
        MetaSecretProtector? protector = null,
        MetaDbContext? store = null,
        Func<MetaDbContext>? stores = null)
    {
        Protector = protector ?? store?.Protector ?? NewProtector();
        _storeName = store is null ? "meta-" + Guid.NewGuid() : null;
        _stores = stores;
        Db = store ?? NewInMemoryDb(_storeName!, Protector, _root);
        Secrets = secrets ?? new SecretGenerator();
        Placement = new PlacementService(Db);
        Creator = new InstanceCreator(Db, Placement, Secrets, Options, Clock);
        Provisioner = NewProvisioner(Db);
    }

    private readonly string? _storeName;
    private readonly Func<MetaDbContext>? _stores;
    private readonly InMemoryDatabaseRoot _root = new();

    /// <summary>
    /// A provisioner over another context but this harness's fakes, clock, and options —
    /// a second actor on the same instance (a second operator's request, a reconciler
    /// sweep), which needs its own context because a <see cref="MetaDbContext"/> is not
    /// safe to use concurrently.
    /// </summary>
    public InstanceProvisioner NewProvisioner(MetaDbContext store) =>
        new(store, new FakeSubstrateFactory(Substrate), Caddy, new InstanceRecipe(Options),
            new InstanceHealthProbe(new HttpClient(new AlwaysOkHandler())), Options, Clock,
            NullLogger<InstanceProvisioner>.Instance);

    /// <summary>
    /// A second context over the same store. The explicit <see cref="InMemoryDatabaseRoot"/>
    /// is what makes "the same store" true: without one, two independently built option sets
    /// get their own internal service provider and therefore their own database.
    /// </summary>
    public MetaDbContext NewStore() =>
        _stores?.Invoke()
        ?? (_storeName is null
            ? throw new InvalidOperationException(
                "this harness was built over an external store with no factory; pass `stores:`")
            : NewInMemoryDb(_storeName, Protector, _root));

    /// <summary>
    /// The DI seam <see cref="InstanceReconciler"/> resolves per sweep, over this harness's
    /// store and fakes. Each scope gets its own context, as a real request scope would get
    /// its own connection, so what a sweep writes is visible to the test and vice versa.
    /// </summary>
    public IServiceScopeFactory Scopes => _scopes ??= new HarnessScopes(this);
    private HarnessScopes? _scopes;

    /// <summary>A reconciler wired to this harness — sweep it directly rather than running its timer.</summary>
    public InstanceReconciler NewReconciler() =>
        new(Scopes, Tracker, Clock, NullLogger<InstanceReconciler>.Instance);

    private sealed class HarnessScopes(SagaHarness harness) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(harness);

        private sealed class Scope : IServiceScope, IServiceProvider
        {
            private readonly SagaHarness _harness;
            private readonly MetaDbContext _db;

            public Scope(SagaHarness harness)
            {
                _harness = harness;
                _db = harness.NewStore();
            }

            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type serviceType) =>
                serviceType == typeof(MetaDbContext) ? _db
                : serviceType == typeof(InstanceProvisioner) ? _harness.NewProvisioner(_db)
                : null;

            public void Dispose() => _db.Dispose();
        }
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

    /// <summary>A protector over one fresh random key — the default for tests that don't care which.</summary>
    public static MetaSecretProtector NewProtector() => new([MetaSecretProtector.NewKey()]);

    /// <summary>
    /// An in-memory store wired like the real one. Goes through
    /// <see cref="MetaDbContext.Configure"/> so the model cache is keyed on the
    /// protector's key — without it, two contexts on different keys in one test run
    /// would share the first one's converters (and therefore its key).
    /// </summary>
    public static MetaDbContext NewInMemoryDb(
        string name, MetaSecretProtector protector, InMemoryDatabaseRoot? root = null)
    {
        var b = root is null
            ? new DbContextOptionsBuilder<MetaDbContext>().UseInMemoryDatabase(name)
            : new DbContextOptionsBuilder<MetaDbContext>().UseInMemoryDatabase(name, root);
        MetaDbContext.Configure(b);
        return new MetaDbContext(b.Options, protector);
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
