using System.Security.Cryptography.X509Certificates;

namespace Landbridge.Preview;

/// <summary>
/// Loads the wildcard-cert PEM that terminates TLS on <c>*.{Domain}</c> (spec
/// §8.4: a provided PEM to start, ACME DNS-01 later). Returns null when no cert
/// is configured — the frontend then runs in plaintext (local runs, or behind a
/// TLS-terminating load balancer). Once loaded, <see cref="PreviewCertificateProvider"/>
/// watches the same paths and reloads on renewal without a restart.
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

        return LoadFromFiles(options.CertPemPath, options.CertKeyPemPath);
    }

    /// <summary>
    /// Loads a cert+key PEM pair from explicit paths. Throws if either file is
    /// missing, unparseable, or half-written, or if the private key does not
    /// correspond to the certificate's public key — <see cref="X509Certificate2.CreateFromPemFile"/>
    /// validates that correspondence, which is exactly what makes reloading a
    /// non-atomically-written pair safe: a stale-cert/fresh-key (or vice-versa)
    /// snapshot mid-renewal fails to combine and is rejected by the caller.
    /// </summary>
    public static X509Certificate2 LoadFromFiles(string certPemPath, string keyPemPath)
    {
        // Round-trip through PKCS#12 so the resulting cert carries a private key
        // SslStream can use for server auth on every OS (the PEM-loaded key is
        // ephemeral and rejected as a server cert on Windows otherwise).
        using var pem = X509Certificate2.CreateFromPemFile(certPemPath, keyPemPath);
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), password: null);
    }
}
