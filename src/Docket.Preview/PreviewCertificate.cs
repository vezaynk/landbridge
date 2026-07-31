using System.Security.Cryptography.X509Certificates;

namespace Docket.Preview;

/// <summary>
/// Loads the wildcard-cert PEM that terminates TLS on <c>*.{Domain}</c> (spec
/// §8.4: a provided PEM to start, ACME DNS-01 later). Returns null when no cert
/// is configured — the frontend then runs in plaintext (local runs, or behind a
/// TLS-terminating load balancer).
/// </summary>
internal static class PreviewCertificate
{
    public static X509Certificate2? Load(PreviewOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.CertPemPath))
            return null;
        if (string.IsNullOrWhiteSpace(options.CertKeyPemPath))
            throw new InvalidOperationException(
                $"{PreviewOptions.SectionName}:CertPemPath is set but {PreviewOptions.SectionName}:CertKeyPemPath is not; " +
                "a server certificate needs its private key.");

        // Round-trip through PKCS#12 so the resulting cert carries a private key
        // SslStream can use for server auth on every OS (the PEM-loaded key is
        // ephemeral and rejected as a server cert on Windows otherwise).
        using var pem = X509Certificate2.CreateFromPemFile(options.CertPemPath, options.CertKeyPemPath);
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), password: null);
    }
}
