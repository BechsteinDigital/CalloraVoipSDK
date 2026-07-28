using System.Security.Cryptography.X509Certificates;
using CalloraVoipSdk.Core.Application.Ports.Security;

namespace CalloraVoipSdk.Core.Infrastructure.Security;

/// <summary>
/// Infrastructure helper that realizes the certificate behavior for a
/// <see cref="TlsConfiguration"/> data contract: lazy, cached loading of the
/// configured X.509 identity certificate from disk and RFC 5922 §7.1 SIP-domain
/// SAN validation of a peer certificate.
/// </summary>
/// <remarks>
/// The certificate is loaded at most once per provider instance: concurrent first
/// callers of <see cref="GetCertificate"/> synchronize on an internal lock (double-checked
/// locking) so two callers cannot both construct an <see cref="X509Certificate2"/>,
/// which would leak the loser instance.
/// </remarks>
internal sealed class SipTlsCertificateProvider
{
    private readonly TlsConfiguration _configuration;
    private readonly object _certificateSync = new();
    private X509Certificate2? _certificate;

    /// <summary>
    /// Creates a provider bound to the supplied TLS configuration DTO.
    /// </summary>
    /// <param name="configuration">The TLS configuration data contract. Must not be <see langword="null"/>.</param>
    internal SipTlsCertificateProvider(TlsConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>
    /// Returns the configured X.509 certificate, loading it from disk on first
    /// call. Returns <see langword="null"/> when no certificate path is configured.
    /// Thread-safe: concurrent first calls load the certificate exactly once.
    /// </summary>
    public X509Certificate2? GetCertificate()
    {
        if (_configuration.CertificatePath == null)
            return null;

        // Double-checked load under a lock so two concurrent callers cannot both construct an
        // X509Certificate2 (the loser would be leaked, never disposed).
        if (_certificate != null)
            return _certificate;

        lock (_certificateSync)
        {
            _certificate ??= LoadCertificate(_configuration.CertificatePath, _configuration.CertificatePassword);
        }

        return _certificate;
    }

    private static X509Certificate2 LoadCertificate(string path, string? password)
    {
#if NET9_0_OR_GREATER
        // X509CertificateLoader replaces the obsolete X509Certificate2(path, password) constructor
        // (SYSLIB0057). TLS identity certificates carry a private key, i.e. they are PKCS#12/PFX.
        return X509CertificateLoader.LoadPkcs12FromFile(path, password);
#else
        return new X509Certificate2(path, password);
#endif
    }

    /// <summary>
    /// Validates that <paramref name="certificate"/> satisfies the RFC 5922
    /// SIP domain check configured via <see cref="TlsConfiguration.ExpectedSipDomain"/>.
    /// </summary>
    /// <param name="certificate">The peer X.509 certificate to validate.</param>
    /// <returns>
    /// <see langword="true"/> when <see cref="TlsConfiguration.ExpectedSipDomain"/> is not set
    /// (validation skipped) or when the certificate's SAN matches the expected
    /// domain; <see langword="false"/> when the SAN check fails.
    /// </returns>
    /// <remarks>
    /// This method is intended to be called from a
    /// <see cref="System.Net.Security.SslStream"/> certificate validation
    /// callback <em>after</em> the standard chain and hostname checks have
    /// passed, to add RFC 5922 SIP domain identity verification.
    /// </remarks>
    public bool ValidatePeerCertificateSipDomain(X509Certificate2 certificate)
    {
        if (string.IsNullOrWhiteSpace(_configuration.ExpectedSipDomain))
            return true; // RFC 5922 SAN check not configured — skip

        return SipDomainCertificateValidator.ValidateSipDomain(certificate, _configuration.ExpectedSipDomain);
    }
}
