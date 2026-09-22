using Landbridge.Auth;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var connectionString = builder.Configuration.GetConnectionString("Landbridge")
    ?? Environment.GetEnvironmentVariable("LANDBRIDGE_DB")
    ?? "Host=localhost;Database=landbridge;Username=landbridge";

builder.Services.AddDbContextFactory<LandbridgeDbContext>(o =>
    o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IDbContextFactory<LandbridgeDbContext>>().CreateDbContext());
builder.Services.AddSingleton(TimeProvider.System);

// The credential services. TokenService is what a completed flow calls to mint the
// human session — the same call the dashboard login makes — so this host writes
// credentials and nothing else. No SessionStore, no Apply, no registry.
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<OAuthAuthorizationCodeService>();

// The operator verifier caches the configured passphrase hash; the CIMD client
// holds one SSRF-fenced HttpClient. The insecure flag relaxes the https/private-host
// guard for dev/test loopback fetches only — off by default.
builder.Services.AddSingleton<IOperatorVerifier, ConfiguredOperatorVerifier>();
builder.Services.AddSingleton<ICimdClient>(sp =>
    new CimdClient(sp.GetRequiredService<IConfiguration>().GetValue<bool>(CimdClient.AllowInsecureKey)));
builder.Services.AddSingleton<OperatorAttemptLimiter>();

// This host is the issuer. Its own public URL is what RFC 8414 metadata must
// carry, and the resource id is the MCP surface it mints tokens for — the two are
// separate values now that they are separate hosts (§5, §13).
var authUrl = builder.Configuration["Landbridge:AuthUrl"]
    ?? Environment.GetEnvironmentVariable("LANDBRIDGE_AUTH_URL");
var publicMcpUrl = builder.Configuration["Landbridge:PublicMcpUrl"]
    ?? Environment.GetEnvironmentVariable("LANDBRIDGE_PUBLIC_MCP_URL")
    ?? DispatchService.DefaultPublicMcpUrl;
builder.Services.AddSingleton(OAuthServerConfig.FromPublicMcpUrl(publicMcpUrl, authUrl));

var app = builder.Build();
app.MapDefaultEndpoints();

// RFC 8414 discovery, then the flow itself. No authentication middleware: every
// route here is anonymous by construction — the passphrase on the consent form is
// the human-verification seam, and the token endpoint authenticates by PKCE and a
// one-time code, not by a bearer this host would have to validate.
app.MapOAuthAuthorizationServerMetadata();
app.MapOAuthEndpoints();

// §5 Bootstrap: the machine half of credential issuance. /enroll exchanges a
// human-issued enrollment token for machine credentials; /machine/refresh re-mints
// landbridged's short-lived access token. Both anonymous — the presented token is
// the credential — and both are TokenService calls, which is the whole reason they
// belong beside the authorization server rather than in the process that owns Apply.
app.MapEnrollmentEndpoints();
app.Run();

/// <summary>Exposed so a test host can construct the app.</summary>
public partial class Program;
