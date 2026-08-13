using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
using Docket.Mcp.Auth;
using Docket.Mcp.Tools;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Docket.Mcp.Tests;

/// <summary>
/// Hosts and helpers for the OAuth 2.1 human-flow tests (spec §5). The plane is
/// the real <c>docket-mcp</c> pipeline (auth scheme + tools + MCP endpoint) plus
/// the new OAuth surface (well-known metadata, authorize, token), mirroring
/// <c>Program.cs</c> and pointed at the fixture's ephemeral Postgres. Because the
/// OAuth canonical URL (resource id / issuer) is derived from configuration at
/// startup, the plane is bound to a <b>fixed</b> loopback port and told that same
/// URL, so discovery, the 401 challenge, and authorize/token all agree.
///
/// <para>A tiny second loopback server serves a Client ID Metadata Document so the
/// CIMD path is exercised for real; the plane is configured with
/// <c>AllowInsecureClientMetadata=true</c> so its SSRF fetcher accepts the
/// loopback http URL (that relaxation is dev/test only — see
/// <see cref="CimdClient"/>).</para>
/// </summary>
internal static class OAuthTestKit
{
    public const string Passphrase = "operator-correct-horse";

    public static string PassphraseHash =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Passphrase)));

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>
    /// The real MCP plane plus the OAuth endpoints, on a fixed loopback port. When
    /// <paramref name="configureOperator"/> is false the operator passphrase is
    /// left unset, so the authorize endpoint is fail-closed (503) — the negative
    /// case.
    /// </summary>
    public static WebApplication BuildPlane(string connectionString, int port, bool configureOperator)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var url = $"http://127.0.0.1:{port}";
        builder.WebHost.UseUrls(url);

        var cfg = new Dictionary<string, string?>
        {
            ["Docket:PublicMcpUrl"] = url,
            // Dev/test: let the SSRF fetcher accept the loopback CIMD test server.
            [CimdClient.AllowInsecureKey] = "true",
        };
        if (configureOperator)
            cfg[ConfiguredOperatorVerifier.PassphraseHashKey] = PassphraseHash;
        builder.Configuration.AddInMemoryCollection(cfg);

        builder.Services.AddDbContext<DocketDbContext>(o =>
            o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddDocketStore();
        builder.Services.AddScoped<TokenService>();
        builder.Services.AddScoped<RelayGrantService>();
        builder.Services.AddScoped<PreviewMappingService>(); // §8.4: WorkerTools.open_preview
        builder.Services.AddScoped<OAuthAuthorizationCodeService>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<RunnerConnectionRegistry>();
        // §8.3: the forward orchestrator + the lead↔machine binding LeadTools needs.
        builder.Services.AddDocketForwarding();
        builder.Services.AddHttpContextAccessor();

        builder.Services.AddSingleton(OAuthServerConfig.FromPublicMcpUrl(url));
        builder.Services.AddSingleton<IOperatorVerifier, ConfiguredOperatorVerifier>();
        builder.Services.AddSingleton<ICimdClient>(sp =>
            new CimdClient(sp.GetRequiredService<IConfiguration>().GetValue<bool>(CimdClient.AllowInsecureKey)));

        builder.Services.AddAuthentication(DocketAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, DocketAuthenticationHandler>(
                DocketAuthenticationHandler.SchemeName, configureOptions: null);
        builder.Services.AddAuthorization();

        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<WorkerTools>()
            .WithTools<LeadTools>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMcp().RequireAuthorization();
        app.MapOAuthMetadataEndpoints();
        app.MapOAuthEndpoints();
        return app;
    }

    /// <summary>
    /// The <c>client_uri</c> the default CIMD fixture advertises. Deliberately a host
    /// the document does <b>not</b> live on: <c>client_uri</c> is unvalidated document
    /// body, so a fixture that set it to its own <c>client_id</c> origin would let a
    /// consent-page assertion pass no matter which of the two the page actually read.
    /// With them divergent, any test asserting on the displayed host is pinned to the
    /// integrity-checked <c>client_id</c>.
    /// </summary>
    public const string UnverifiedClientUri = "https://claimed-site.example";

    /// <summary>
    /// A loopback server serving one Client ID Metadata Document at
    /// <c>/client.json</c>; returns the app and the <c>client_id</c> URL to present.
    /// The document's <c>client_id</c> equals that URL exactly, as CIMD requires,
    /// while its <c>client_uri</c> points at <see cref="UnverifiedClientUri"/>.
    /// </summary>
    public static (WebApplication App, string ClientId) BuildCimdServer(string clientName, params string[] redirectUris) =>
        BuildCimdServer(clientName, UnverifiedClientUri, redirectUris);

    /// <summary>
    /// As <see cref="BuildCimdServer(string, string[])"/>, but with the document's
    /// <c>client_uri</c> (and <c>client_name</c>) chosen by the caller — the two
    /// unverified fields, so a test can serve the impersonation shape: a real
    /// <c>client_id</c> the attacker owns, dressed in a trusted client's name and site.
    /// </summary>
    public static (WebApplication App, string ClientId) BuildCimdServer(
        string clientName, string? clientUri, string[] redirectUris)
    {
        var port = FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var url = $"http://127.0.0.1:{port}";
        builder.WebHost.UseUrls(url);

        var app = builder.Build();
        var clientId = $"{url}/client.json";
        var doc = new
        {
            client_id = clientId,
            client_name = clientName,
            client_uri = clientUri,
            redirect_uris = redirectUris,
            token_endpoint_auth_method = "none",
            application_type = "native",
        };
        // No naming policy: the JSON keys stay exactly the snake_case CIMD names.
        app.MapGet("/client.json", () => Results.Json(doc, new JsonSerializerOptions()));
        return (app, clientId);
    }

    public static string BaseUrl(WebApplication app) =>
        app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal));

    public static async Task<McpClient> ConnectMcpAsync(Uri endpoint, string bearer, CancellationToken ct)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearer}" },
        });
        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    // ── PKCE (RFC 7636) client side ───────────────────────────────────────────

    public static string NewCodeVerifier() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string S256Challenge(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
