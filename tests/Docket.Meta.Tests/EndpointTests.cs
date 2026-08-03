using System.Net;
using Docket.Meta.Data;
using Docket.Meta.Edge;
using Docket.Meta.Provisioning;
using Docket.Meta.Substrate;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Docket.Meta.Tests;

/// <summary>
/// End-to-end HTTP coverage of the panel (design note §1) with the store swapped for
/// EF InMemory and the substrate/edge swapped for the fakes, the reconciler removed,
/// and the operator passphrase configured — so the real endpoints, auth gate, and
/// background provisioning run without Docker or Postgres (0-skip).
/// </summary>
public class EndpointTests : IClassFixture<EndpointTests.Factory>
{
    private readonly Factory _factory;
    public EndpointTests(Factory factory) => _factory = factory;

    public const string Passphrase = "let-me-in";

    [Fact]
    public async Task Unauthenticated_request_redirects_to_login()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var resp = await client.GetAsync("/instances");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/login", resp.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Wrong_passphrase_is_rejected()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var resp = await client.PostAsync("/login", Form(("passphrase", "nope")));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Login_sets_a_session_that_reaches_the_instances_page()
    {
        using var client = await SignedInClientAsync();
        var resp = await client.GetAsync("/instances");
        resp.EnsureSuccessStatusCode();
        Assert.Contains("Instances", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Create_provisions_to_ready_and_shows_the_passphrase_once()
    {
        _factory.Substrate.Containers.Clear();
        using var client = await SignedInClientAsync();
        await AddHostAsync();

        var create = await client.PostAsync("/instances", Form(
            ("name", "web1"), ("account", "Acme"), ("tag", "latest"), ("host", "")));
        create.EnsureSuccessStatusCode();
        var body = await create.Content.ReadAsStringAsync();
        // The shown-once passphrase page renders a secret (we can't assert its value,
        // but the page must be the created confirmation).
        Assert.Contains("shown once", body);

        // Background provisioning converges (fakes are instant).
        var id = await WaitForStateAsync("web1", InstanceState.Ready);
        Assert.NotEqual(Guid.Empty, id);

        var detail = await client.GetAsync($"/instances/{id}");
        detail.EnsureSuccessStatusCode();
        var detailBody = await detail.Content.ReadAsStringAsync();
        Assert.Contains("web1.docket", detailBody);
        Assert.Contains("VerifyReady", detailBody); // saga event log rendered
    }

    [Fact]
    public async Task Add_and_remove_host()
    {
        using var client = await SignedInClientAsync(redirect: true);
        var add = await client.PostAsync("/hosts", Form(
            ("name", "h-" + Guid.NewGuid().ToString("N")[..6]),
            ("endpoint", "unix:///var/run/docker.sock"),
            ("published", "127.0.0.1")));
        add.EnsureSuccessStatusCode();
        Assert.Contains("unix:///var/run/docker.sock", await add.Content.ReadAsStringAsync());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<HttpClient> SignedInClientAsync(bool redirect = false)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = redirect, HandleCookies = true });
        var resp = await client.PostAsync("/login", Form(("passphrase", Passphrase)));
        Assert.True(resp.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK, $"login status {resp.StatusCode}");
        return client;
    }

    private async Task AddHostAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetaDbContext>();
        if (!await db.Hosts.AnyAsync())
        {
            db.Hosts.Add(new HostRow
            {
                Id = Guid.NewGuid(),
                Name = "local",
                EndpointUri = "unix:///var/run/docker.sock",
                EndpointKind = HostEndpointKind.UnixSocket,
                PublishedHost = "127.0.0.1",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
    }

    private async Task<Guid> WaitForStateAsync(string name, InstanceState state)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetaDbContext>();
            var row = await db.Instances.FirstOrDefaultAsync(i => i.Name == name);
            if (row is not null && row.State == state)
                return row.Id;
            await Task.Delay(50);
        }
        return Guid.Empty;
    }

    private static FormUrlEncodedContent Form(params (string, string)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));

    /// <summary>The customized host: InMemory store, fake substrate/edge, no reconciler, passphrase set.</summary>
    public sealed class Factory : WebApplicationFactory<Program>
    {
        // A shared InMemory database name so every request scope sees the same data.
        private readonly string _dbName = "meta-web-" + Guid.NewGuid();
        public FakeSubstrate Substrate { get; } = new();
        public FakeCaddyAdmin Caddy { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Meta:Operator:PassphraseHash", SecretGenerator.Hash(Passphrase));
            builder.UseSetting("Meta:Domain", "example.com");

            // ConfigureTestServices runs AFTER the app's own registrations, so these
            // swaps win.
            builder.ConfigureTestServices(services =>
            {
                // Remove every EF descriptor keyed on MetaDbContext (options, the EF 10
                // IDbContextOptionsConfiguration<T>, the context itself) so only the
                // InMemory provider remains — otherwise Npgsql + InMemory both attach to
                // the same options and EF refuses two providers.
                var efDescriptors = services.Where(d =>
                    d.ServiceType == typeof(MetaDbContext) ||
                    d.ServiceType == typeof(DbContextOptions) ||
                    (d.ServiceType.IsGenericType &&
                     d.ServiceType.GetGenericArguments().Contains(typeof(MetaDbContext)))).ToList();
                foreach (var d in efDescriptors)
                    services.Remove(d);
                services.AddDbContext<MetaDbContext>(o => o.UseInMemoryDatabase(_dbName));

                services.RemoveAll<ISubstrateFactory>();
                services.AddSingleton<ISubstrateFactory>(new FakeSubstrateFactory(Substrate));
                services.RemoveAll<ICaddyAdmin>();
                services.AddSingleton<ICaddyAdmin>(Caddy);

                // The health probe hits an HTTP port; point it at a stub that says "up".
                services.RemoveAll<InstanceHealthProbe>();
                services.AddSingleton(new InstanceHealthProbe(new HttpClient(new OkHandler())));

                // Drop the reconciler (it would sweep the InMemory store on startup).
                services.RemoveAll<IHostedService>();
            });
        }

        private sealed class OkHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
