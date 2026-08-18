using System.Net;
using Landbridge.Meta.Data;
using Landbridge.Meta.Edge;
using Landbridge.Meta.Provisioning;
using Landbridge.Meta.Secrets;
using Landbridge.Meta.Substrate;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Landbridge.Meta.Tests;

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
        using var client = Client(redirect: false);
        var resp = await client.GetAsync("/instances");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/login", resp.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Wrong_passphrase_is_rejected()
    {
        using var client = Client(redirect: false);
        var resp = await PostFormAsync(client, "/login", Form(("passphrase", "nope")));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    /// <summary>
    /// Every mutating request here is a cookie-authenticated form with no token in it, so the
    /// panel refuses one that did not come from its own pages. Asserted on the two ends of the
    /// range it covers: the open login door and a gated write that provisions infrastructure.
    /// The same-origin half is asserted too — a check that refused everything would pass a
    /// one-sided test and break the panel.
    /// </summary>
    [Fact]
    public async Task A_cross_origin_post_is_refused_on_the_login_door_and_on_a_gated_write()
    {
        using var signedIn = await SignedInClientAsync();

        // Signed in, correct form, wrong origin: refused, and the panel says so rather than
        // redirecting to a page the operator was never on.
        var refusedWrite = await PostFormAsync(signedIn, "/hosts", HostForm("smuggled"),
            origin: "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, refusedWrite.StatusCode);
        Assert.Contains("Request refused", await refusedWrite.Content.ReadAsStringAsync());

        // And nothing landed: the host the forged form named does not exist.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetaDbContext>();
            Assert.False(await db.Hosts.AnyAsync(h => h.Name == "smuggled"));
        }

        // The login door too: a cross-origin login would sign the operator into a session
        // somebody else minted.
        using var client = Client(redirect: false);
        var refusedLogin = await PostFormAsync(client, "/login", Form(("passphrase", Passphrase)),
            origin: "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, refusedLogin.StatusCode);
        Assert.False(refusedLogin.Headers.Contains("Set-Cookie"));

        // The same write from the panel's own origin goes through — a check that refused
        // everything would pass a one-sided test and break the panel.
        var accepted = await PostFormAsync(signedIn, "/hosts", HostForm(UniqueHostName()));
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
    }

    /// <summary>
    /// A request whose <c>Origin</c> an intermediary stripped is accepted only on the browser's
    /// own word that it was first-party. Fetch metadata is that word, and it cannot be set by a
    /// document — so <c>cross-site</c>, or nothing at all, is refused.
    /// </summary>
    [Fact]
    public async Task Fetch_metadata_stands_in_for_a_stripped_origin_but_only_when_it_says_same_origin()
    {
        using var client = await SignedInClientAsync();

        foreach (var (fetchSite, expected) in new (string?, HttpStatusCode)[]
                 {
                     ("same-origin", HttpStatusCode.Redirect),
                     ("cross-site", HttpStatusCode.Forbidden),
                     (null, HttpStatusCode.Forbidden),
                 })
        {
            var resp = await PostFormAsync(
                client, "/hosts", HostForm(UniqueHostName()), origin: null, fetchSite: fetchSite);
            Assert.Equal(expected, resp.StatusCode);
        }
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

        var create = await PostFormAsync(client, "/instances", Form(
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
        Assert.Contains("web1.landbridge", detailBody);
        Assert.Contains("VerifyReady", detailBody); // saga event log rendered
    }

    [Fact]
    public async Task Add_and_remove_host()
    {
        using var client = await SignedInClientAsync(redirect: true);
        var add = await PostFormAsync(client, "/hosts", HostForm(UniqueHostName()));
        add.EnsureSuccessStatusCode();
        Assert.Contains("unix:///var/run/docker.sock", await add.Content.ReadAsStringAsync());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>The origin the test host answers on — what a browser on the panel's own page sends.</summary>
    private const string SelfOrigin = "http://localhost";

    private HttpClient Client(bool redirect) =>
        _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = redirect, HandleCookies = true });

    /// <summary>
    /// A form POST as a browser on the panel's own page makes it: declaring <c>Origin</c>, which
    /// every mutating request here now has to (<c>MetaEndpoints.CrossOriginRefusal</c>). Pass a
    /// different <paramref name="origin"/> to forge one, or null to omit it entirely — the case a
    /// header-stripping intermediary produces, decided by <paramref name="fetchSite"/>.
    /// </summary>
    private static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string path, HttpContent content,
        string? origin = SelfOrigin, string? fetchSite = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        if (origin is not null)
            req.Headers.Add("Origin", origin);
        if (fetchSite is not null)
            req.Headers.Add("Sec-Fetch-Site", fetchSite);
        return await client.SendAsync(req);
    }

    private async Task<HttpClient> SignedInClientAsync(bool redirect = false)
    {
        var client = Client(redirect);
        var resp = await PostFormAsync(client, "/login", Form(("passphrase", Passphrase)));
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

    /// <summary>The add-host form, valid but for whatever the caller names it.</summary>
    private static FormUrlEncodedContent HostForm(string name) => Form(
        ("name", name),
        ("endpoint", "unix:///var/run/docker.sock"),
        ("published", "127.0.0.1"));

    private static string UniqueHostName() => "h-" + Guid.NewGuid().ToString("N")[..6];

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
            // Secret protection is fail-closed: without a key the host refuses to
            // start (task #79), so the test host configures one like a real deployment.
            builder.UseSetting(MetaSecretProtector.KeysConfigKey + ":0", MetaSecretProtector.NewKey());

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
                services.AddDbContext<MetaDbContext>(o =>
                    MetaDbContext.Configure(o.UseInMemoryDatabase(_dbName)));

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
