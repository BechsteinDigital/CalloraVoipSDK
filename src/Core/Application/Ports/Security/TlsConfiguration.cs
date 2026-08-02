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

    private readonly SipTlsTrustMode _trustMode = SipTlsTrustMode.System;

    /// <summary>
    /// Trust policy for a remote peer certificate. Defaults to
    /// <see cref="SipTlsTrustMode.System"/> (full chain/hostname validation). The mode governs only
    /// standard trust; a configured <see cref="ExpectedSipDomain"/> and the rejection of a missing
    /// certificate remain fail-closed in every mode.
    /// </summary>
    public SipTlsTrustMode TrustMode
    {
        get => _trustMode;
        init => _trustMode = value;
    }

    /// <summary>
    /// When <see langword="true"/>, the TLS stack accepts server certificates
    /// that fail standard chain or hostname validation. Use only in
    /// development or testing environments.
    /// </summary>
    [Obsolete("Use " + nameof(TrustMode) + " instead. 'true' maps to SipTlsTrustMode.DangerousAcceptAnyChain; " +
        "it never disables the ExpectedSipDomain identity check or the rejection of a missing certificate. " +
        "If both this and TrustMode are set in the same initializer, the last assignment wins.")]
    public bool AcceptUntrustedCertificates
    {
        get => _trustMode == SipTlsTrustMode.DangerousAcceptAnyChain;
        init => _trustMode = value ? SipTlsTrustMode.DangerousAcceptAnyChain : SipTlsTrustMode.System;
    }

    /// <summary>
    /// Optional SIP domain expected in the server certificate's Subject
    /// Alternative Name (SAN) extension per RFC 5922 §7.1.
    /// <para>
    /// When set, the infrastructure TLS provider checks that the peer's
    /// certificate contains an exact <c>dNSName</c> or a <c>sip:</c>
    /// <c>uniformResourceIdentifier</c> SAN that matches this domain per RFC 5922 §7.2
    /// (<c>sips:</c>, userinfo URIs and wildcards are rejected). This check is fail-closed and runs
    /// in every <see cref="TrustMode"/>. Leave <see langword="null"/> to skip RFC 5922 SAN validation.
    /// </para>
    /// </summary>
    public string? ExpectedSipDomain { get; init; }
}
