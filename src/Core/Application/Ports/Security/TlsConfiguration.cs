namespace CalloraVoipSdk.Core.Application.Ports.Security;

/// <summary>
/// TLS configuration for SIP transport connections.
/// <para>
/// Supports both outbound (client) and inbound (server) TLS use-cases. This type is a
/// pure configuration data contract; certificate loading and RFC 5922 SIP-domain
/// validation are performed by the infrastructure TLS provider, not by this DTO.
/// </para>
/// <para>
/// RFC 5922 compliance: set <see cref="ExpectedSipDomain"/> to enable
/// domain certificate validation per RFC 5922 §7.1 in addition to the
/// standard chain and hostname checks performed by the TLS stack.
/// </para>
/// </summary>
public sealed class TlsConfiguration
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
    /// When set, the infrastructure TLS provider checks that the peer's
    /// certificate contains a <c>dNSName</c> or
    /// <c>uniformResourceIdentifier</c> (sip:/sips:) SAN that matches this
    /// domain. Leave <see langword="null"/> to skip RFC 5922 SAN validation.
    /// </para>
    /// </summary>
    public string? ExpectedSipDomain { get; init; }
}
