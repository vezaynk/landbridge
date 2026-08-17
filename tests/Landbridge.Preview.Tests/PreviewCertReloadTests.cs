using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;

namespace Landbridge.Preview.Tests;

/// <summary>
/// Guards the wildcard-cert PEM hot-reload (spec §8.4): an ACME renewal drops a new
/// cert+key on disk and <b>new</b> TLS handshakes must serve it with no restart,
/// while a broken or half-written renewal must never take TLS down. Drives the real
/// <see cref="PreviewServer"/> over TLS on loopback, wired to a
/// <see cref="PreviewCertificateProvider"/> pointed at temp PEM files, and reads
/// which certificate the frontend presents by capturing it in the client's
/// validation callback.
/// </summary>
public sealed class PreviewCertReloadTests
{
    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    [Fact]
    public async Task New_handshakes_serve_the_renewed_cert_after_the_files_change_no_restart()
    {
        using var dir = new TempDir();
        var (certPath, keyPath) = (dir.Path("wild.crt"), dir.Path("wild.key"));
        var a = TestCert.Make("landbridge-preview-A");
        var b = TestCert.Make("landbridge-preview-B");
        await File.WriteAllTextAsync(certPath, a.CertPem, Ct);
        await File.WriteAllTextAsync(keyPath, a.KeyPem, Ct);

        // A real file watcher (short debounce), so this exercises the watch→reload path.
        using var provider = MakeProvider(certPath, keyPath, TimeSpan.FromMilliseconds(100));
        await using var cp = await FakeControlPlane.StartAsync();
        await using var harness = PreviewHarness.Start(cp.Url, certificates: provider);

        // Before renewal, and on a connection we hold open across the swap.
        var (established, establishedThumb) = await OpenTlsAsync(harness.Port, Ct);
        try
        {
            Assert.Equal(a.Thumbprint, establishedThumb);

            // Renewal writes the new pair to the SAME paths; the watcher fires the reload.
            await File.WriteAllTextAsync(keyPath, b.KeyPem, Ct);
            await File.WriteAllTextAsync(certPath, b.CertPem, Ct);

            // New handshakes converge on cert B — no restart.
            await AssertEventuallyServesAsync(harness.Port, b.Thumbprint, Ct);

            // The connection established under cert A is untouched by the swap.
            Assert.True(established.IsAuthenticated);
            Assert.NotEqual(SslProtocols.None, established.SslProtocol);
        }
        finally
        {
            await established.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_broken_swap_keeps_serving_the_old_cert()
    {
        using var dir = new TempDir();
        var (certPath, keyPath) = (dir.Path("wild.crt"), dir.Path("wild.key"));
        var a = TestCert.Make("landbridge-preview-A");
        var b = TestCert.Make("landbridge-preview-B");
        await File.WriteAllTextAsync(certPath, a.CertPem, Ct);
        await File.WriteAllTextAsync(keyPath, a.KeyPem, Ct);

        using var provider = MakeProvider(certPath, keyPath, TimeSpan.FromMilliseconds(100));
        await using var cp = await FakeControlPlane.StartAsync();
        await using var harness = PreviewHarness.Start(cp.Url, certificates: provider);

        Assert.Equal(a.Thumbprint, await HandshakeThumbprintAsync(harness.Port, Ct));

        // A corrupt / half-written key lands. The pair no longer loads; TLS must stay up.
        await File.WriteAllTextAsync(keyPath, "-----BEGIN PRIVATE KEY-----\nnot-a-real-key\n-----END PRIVATE KEY-----\n", Ct);
        provider.ReloadForTest(); // deterministic: no watcher race
        Assert.Equal(a.Thumbprint, await HandshakeThumbprintAsync(harness.Port, Ct));

        // And the fail-safe didn't wedge us: a subsequent valid renewal still swaps in.
        await File.WriteAllTextAsync(keyPath, b.KeyPem, Ct);
        await File.WriteAllTextAsync(certPath, b.CertPem, Ct);
        provider.ReloadForTest();
        Assert.Equal(b.Thumbprint, await HandshakeThumbprintAsync(harness.Port, Ct));
    }

    [Fact]
    public async Task Two_file_renewal_written_key_then_cert_settles_on_the_new_cert()
    {
        using var dir = new TempDir();
        var (certPath, keyPath) = (dir.Path("wild.crt"), dir.Path("wild.key"));
        var a = TestCert.Make("landbridge-preview-A");
        var b = TestCert.Make("landbridge-preview-B");
        await File.WriteAllTextAsync(certPath, a.CertPem, Ct);
        await File.WriteAllTextAsync(keyPath, a.KeyPem, Ct);

        using var provider = MakeProvider(certPath, keyPath, TimeSpan.FromMilliseconds(100));
        await using var cp = await FakeControlPlane.StartAsync();
        await using var harness = PreviewHarness.Start(cp.Url, certificates: provider);

        Assert.Equal(a.Thumbprint, await HandshakeThumbprintAsync(harness.Port, Ct));

        // Renewal writes the KEY first: disk now holds keyB + certA — a mismatch. The
        // reload must reject it and keep serving A (never a mismatched half-cert).
        await File.WriteAllTextAsync(keyPath, b.KeyPem, Ct);
        provider.ReloadForTest();
        Assert.Equal(a.Thumbprint, await HandshakeThumbprintAsync(harness.Port, Ct));

        // Then the CERT lands: keyB + certB now agree, so the pair swaps in.
        await File.WriteAllTextAsync(certPath, b.CertPem, Ct);
        provider.ReloadForTest();
        Assert.Equal(b.Thumbprint, await HandshakeThumbprintAsync(harness.Port, Ct));
    }

    private static PreviewCertificateProvider MakeProvider(string certPath, string keyPath, TimeSpan debounce) =>
        PreviewCertificateProvider.Create(
            new PreviewOptions { CertPemPath = certPath, CertKeyPemPath = keyPath, CertReloadDebounce = debounce },
            NullLogger<PreviewCertificateProvider>.Instance)!;

    private static async Task AssertEventuallyServesAsync(int port, string expectedThumbprint, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var last = "";
        while (DateTime.UtcNow < deadline)
        {
            last = await HandshakeThumbprintAsync(port, ct);
            if (string.Equals(last, expectedThumbprint, StringComparison.Ordinal))
                return;
            await Task.Delay(50, ct);
        }
        Assert.Fail($"frontend never served {expectedThumbprint}; last handshake saw {last}");
    }

    private static async Task<string> HandshakeThumbprintAsync(int port, CancellationToken ct)
    {
        var (tls, thumbprint) = await OpenTlsAsync(port, ct);
        await tls.DisposeAsync();
        return thumbprint;
    }

    /// <summary>TLS-handshakes the frontend and captures the presented server cert's thumbprint.</summary>
    private static async Task<(SslStream Tls, string Thumbprint)> OpenTlsAsync(int port, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, port, ct);
        var thumbprint = "";
        var tls = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false,
            (_, cert, _, _) =>
            {
                thumbprint = cert!.GetCertHashString(); // == X509Certificate2.Thumbprint (uppercase hex)
                return true;                            // self-signed under test: assert on identity, not trust
            });
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "label1.preview.localhost",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, ct);
        return (tls, thumbprint);
    }

    /// <summary>A self-signed cert as the two PEM strings an ACME client would leave on disk.</summary>
    private sealed record TestCert(string CertPem, string KeyPem, string Thumbprint)
    {
        public static TestCert Make(string commonName)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                $"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("label1.preview.localhost");
            san.AddDnsName("preview.localhost");
            request.CertificateExtensions.Add(san.Build());
            using var cert = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            return new TestCert(cert.ExportCertificatePem(), rsa.ExportPkcs8PrivateKeyPem(), cert.Thumbprint);
        }
    }

    private sealed class TempDir : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("landbridge-preview-cert-").FullName;
        public string Path(string name) => System.IO.Path.Combine(_dir, name);
        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
