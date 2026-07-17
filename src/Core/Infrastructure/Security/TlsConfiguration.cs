using System.Security.Cryptography.X509Certificates;

namespace CalloraVoipSdk.Core.Infrastructure.Security;

/// <summary>
/// TLS configuration for SIP transport connections.
/// <para>
/// Supports both outbound (client) and inbound (server) TLS use-cases.
/// Certificate loading is lazy and cached after the first call to
/// <see cref="GetCertificate"/>.
/// </para>
/// <para>
/// RFC 5922 compliance: set <see cref="ExpectedSipDomain"/> to enable
/// domain certificate validation per RFC 5922 §7.1 in addition to the
/// standard chain and hostname checks performed by the TLS stack.
/// </para>
/// </summary>
public class TlsConfiguration
{
    /// <summary>
    /// Path to the X.509 certificate file (PFX/P12 or PEM).
    /// </summary>
    public string? CertificatePath { get; init; }

    /// <summary>
    /// Password for the certificate file, if required.
    /// </summary>
    public string? CertificatePassword { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the TLS stack accepts server certificates
    /// that fail standard chain or hostname validation. Use only in
    /// development or testing environments.
    /// </summary>
    public bool AcceptUntrustedCertificates { get; init; } = false;

    /// <summary>
    /// Optional SIP domain expected in the server certificate's Subject
    /// Alternative Name (SAN) extension per RFC 5922 §7.1.
    /// <para>
    /// When set, <see cref="ValidatePeerCertificateSipDomain"/> checks that
    /// the peer's certificate contains a <c>dNSName</c> or
    /// <c>uniformResourceIdentifier</c> (sip:/sips:) SAN that matches this
    /// domain. Leave <see langword="null"/> to skip RFC 5922 SAN validation.
    /// </para>
    /// </summary>
    public string? ExpectedSipDomain { get; init; }

    private X509Certificate2? _certificate;

    /// <summary>
    /// Returns the configured X.509 certificate, loading it from disk on first
    /// call. Returns <see langword="null"/> when no certificate path is configured.
    /// </summary>
    public X509Certificate2? GetCertificate()
    {
        if (_certificate != null)
            return _certificate;

        if (CertificatePath == null)
            return null;

        _certificate = LoadCertificate(CertificatePath, CertificatePassword);
        return _certificate;
    }

    private static X509Certificate2 LoadCertificate(string path, string? password)
    {
#pragma warning disable SYSLIB0057
        return new X509Certificate2(path, password);
#pragma warning restore SYSLIB0057
    }

    /// <summary>
    /// Validates that <paramref name="certificate"/> satisfies the RFC 5922
    /// SIP domain check configured via <see cref="ExpectedSipDomain"/>.
    /// </summary>
    /// <param name="certificate">The peer X.509 certificate to validate.</param>
    /// <returns>
    /// <see langword="true"/> when <see cref="ExpectedSipDomain"/> is not set
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
        if (string.IsNullOrWhiteSpace(ExpectedSipDomain))
            return true; // RFC 5922 SAN check not configured — skip

        return SipDomainCertificateValidator.ValidateSipDomain(certificate, ExpectedSipDomain);
    }
}
