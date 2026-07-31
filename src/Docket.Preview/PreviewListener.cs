using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Docket.Preview;

/// <summary>
/// Hosts the <see cref="PreviewServer"/> in the ASP.NET generic host (spec §8.4).
/// Binds the configured port on all interfaces — the preview frontend is a public
/// TLS frontend, unlike <c>docketd</c> which only ever binds loopback (§10) — loads
/// the wildcard cert once, and starts the accept loop. Tests construct a
/// <see cref="PreviewServer"/> directly on a loopback port instead.
/// </summary>
public sealed class PreviewListener : BackgroundService
{
    private readonly PreviewOptions _options;
    private readonly PreviewControlPlaneClient _controlPlane;
    private readonly ILoggerFactory _loggerFactory;
    private PreviewServer? _server;

    public PreviewListener(
        IOptions<PreviewOptions> options,
        PreviewControlPlaneClient controlPlane,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _controlPlane = controlPlane;
        _loggerFactory = loggerFactory;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        X509Certificate2? cert = PreviewCertificate.Load(_options);
        _server = new PreviewServer(
            new IPEndPoint(IPAddress.Any, _options.ListenPort),
            cert,
            _options,
            _controlPlane,
            _loggerFactory.CreateLogger<PreviewServer>());
        _server.Start();
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server is not null)
            await _server.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }
}
