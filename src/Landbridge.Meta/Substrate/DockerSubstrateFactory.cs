using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Docker.DotNet;
using Docket.Meta.Data;

namespace Docket.Meta.Substrate;

/// <summary>
/// Builds and caches a Docker.DotNet client per host (design note §3). A local host
/// is a unix socket (the pool-of-one); a remote host is the Engine API over TCP with
/// mutual TLS, the client cert + CA coming from the host record. Cached because a
/// <see cref="DockerClient"/> is a pooled HTTP client, not a per-call object.
/// </summary>
public sealed class DockerSubstrateFactory : ISubstrateFactory, IDisposable
{
    private readonly ConcurrentDictionary<Guid, (IDockerClient Client, ISubstrate Substrate)> _cache = new();

    public ISubstrate For(HostRow host) =>
        _cache.GetOrAdd(host.Id, _ => Build(host)).Substrate;

    private static (IDockerClient, ISubstrate) Build(HostRow host)
    {
        var config = host.EndpointKind == HostEndpointKind.TcpMtls
            ? new DockerClientConfiguration(new Uri(host.EndpointUri), BuildCredentials(host))
            : new DockerClientConfiguration(new Uri(host.EndpointUri));
        var client = config.CreateClient();
        return (client, new DockerSubstrate(client));
    }

    // Remote mTLS is operator-configured and cannot be exercised in the unit/E2E legs
    // (the E2E runs against the local socket), so this path is documented as
    // operator-tested (docs/META.md). This Docker.DotNet build ships only the base
    // Credentials abstraction (no CertificateCredentials), so mTLS is a small
    // Credentials subclass that decorates the client's HTTP handler with the client
    // cert and pins server trust to the configured CA.
    private static Credentials BuildCredentials(HostRow host)
    {
        if (string.IsNullOrWhiteSpace(host.TlsClientCertPem) || string.IsNullOrWhiteSpace(host.TlsClientKeyPem))
            throw new InvalidOperationException($"host {host.Name}: TcpMtls requires client cert + key PEMs");

        // Round-trip through PKCS#12 so the private key is durably associated with the
        // cert across platforms (the raw CreateFromPem key set can be ephemeral).
        var loaded = X509Certificate2.CreateFromPem(host.TlsClientCertPem, host.TlsClientKeyPem);
        var clientCert = X509CertificateLoader.LoadPkcs12(loaded.Export(X509ContentType.Pfx), password: null);
        var ca = string.IsNullOrWhiteSpace(host.TlsCaPem) ? null : X509Certificate2.CreateFromPem(host.TlsCaPem);
        return new MtlsCredentials(clientCert, ca);
    }

    public void Dispose()
    {
        foreach (var (client, _) in _cache.Values)
            client.Dispose();
        _cache.Clear();
    }

    /// <summary>Presents a client certificate and validates the server against a pinned CA.</summary>
    private sealed class MtlsCredentials(X509Certificate2 clientCert, X509Certificate2? ca) : Credentials
    {
        public override bool IsTlsCredentials() => true;

        public override HttpMessageHandler GetHandler(HttpMessageHandler innerHandler)
        {
            switch (innerHandler)
            {
                case HttpClientHandler h:
                    h.ClientCertificates.Add(clientCert);
                    if (ca is not null)
                        h.ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null && ChainsToCa(cert);
                    break;
                case SocketsHttpHandler s:
                    s.SslOptions.ClientCertificates ??= new X509CertificateCollection();
                    s.SslOptions.ClientCertificates.Add(clientCert);
                    if (ca is not null)
                        s.SslOptions.RemoteCertificateValidationCallback =
                            (_, cert, _, _) => cert is not null &&
                                ChainsToCa(X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert)));
                    break;
            }
            return innerHandler;
        }

        private bool ChainsToCa(X509Certificate2 serverCert)
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca!);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(serverCert);
        }
    }
}
