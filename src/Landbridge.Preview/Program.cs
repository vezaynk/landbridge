using Landbridge.Preview;

var builder = WebApplication.CreateBuilder(args);

// The HTTP preview frontend is a standalone, separately-deployable module (spec
// §3/§8.4): its own ASP.NET host, not part of Landbridge.Mcp and not a change to the
// pure byte-splicer Landbridge.Relay. It reuses the shared Aspire service defaults
// (health, OTel) like the other hosts; the OTLP exporter only activates when
// OTEL_EXPORTER_OTLP_ENDPOINT is set.
builder.AddServiceDefaults();

builder.Services.AddOptions<PreviewOptions>()
    .Bind(builder.Configuration.GetSection(PreviewOptions.SectionName));

// The frontend's client for the plane's /preview/connect endpoint (§8.4). A named
// client so a host can attach its own resilience/handlers to just this traffic.
builder.Services.AddHttpClient(PreviewControlPlaneClient.HttpClientName);
builder.Services.AddSingleton<PreviewControlPlaneClient>();

// The accept loop runs as a hosted service; its raw TcpListener owns the TLS port
// (§8.4) rather than Kestrel, so the served app's HTTP is spliced, never rewritten.
builder.Services.AddHostedService<PreviewListener>();

var app = builder.Build();

// Aspire health endpoints (/health, /alive), mapped in Development only. These run
// on Kestrel's own default port, separate from the preview TcpListener.
app.MapDefaultEndpoints();

app.Run();

/// <summary>Exposed so a host-based test could construct the app, mirroring the other modules.</summary>
public partial class Program;
