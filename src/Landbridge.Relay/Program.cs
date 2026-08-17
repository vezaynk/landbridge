using Landbridge.Relay;

var builder = WebApplication.CreateBuilder(args);

// landbridge-relay is a standalone, separately-deployable module (spec §3): its own
// ASP.NET host, not part of Landbridge.Mcp. It reuses the shared Aspire service
// defaults (health, OTel) like the other hosts; the OTLP exporter only activates
// when OTEL_EXPORTER_OTLP_ENDPOINT is set.
builder.AddServiceDefaults();

builder.Services.AddRelay(builder.Configuration);

var app = builder.Build();

// Aspire health endpoints (/health, /alive), mapped in Development only.
app.MapDefaultEndpoints();

// The tunnel is an HTTP upgrade to a WebSocket (spec §8.3).
app.UseWebSockets();
app.MapRelayTunnel();

app.Run();

/// <summary>Exposed so a WebApplicationFactory-based test can host the app, mirroring Landbridge.Mcp.</summary>
public partial class Program;
