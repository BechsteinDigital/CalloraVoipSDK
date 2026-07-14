using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;

namespace CalloraVoipSdk.Core.Infrastructure.Dtls;

/// <summary>
/// DTLS 1.2 server for DTLS-SRTP (RFC 5764): requires the client to offer <c>use_srtp</c>,
/// mirrors exactly one supported protection profile, requests and fingerprint-verifies the
/// client certificate (RFC 5763 §6.7.1), and exports the SRTP master keys when the
/// handshake completes.
/// </summary>
internal sealed class DtlsSrtpServer : DefaultTlsServer
{
    // The local identity is an ECDSA P-256 certificate, so only ECDHE_ECDSA suites are
    // servable — the DefaultTlsServer defaults would happily pick an RSA suite and then
    // die with internal_error when asked for RSA signer credentials. AES-128-GCM first:
    // the WebRTC/libwebrtc default.
    private static readonly int[] EcdsaCipherSuites =
    {
        CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
        CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
        CipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
        CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256,
    };

    private readonly TlsCrypto _crypto;
    private readonly DtlsCertificate _localCertificate;
    private readonly DtlsFingerprint _expectedRemoteFingerprint;
    private int _selectedProfile;

    public DtlsSrtpServer(
        TlsCrypto crypto,
        DtlsCertificate localCertificate,
        DtlsFingerprint expectedRemoteFingerprint)
        : base(crypto)
    {
        ArgumentNullException.ThrowIfNull(localCertificate);
        ArgumentNullException.ThrowIfNull(expectedRemoteFingerprint);
        _crypto = crypto;
        _localCertificate = localCertificate;
        _expectedRemoteFingerprint = expectedRemoteFingerprint;
    }

    /// <summary>SRTP keys exported after <see cref="NotifyHandshakeComplete"/>.</summary>
    public DtlsSrtpNegotiatedKeys? NegotiatedKeys { get; private set; }

    /// <inheritdoc />
    protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.DTLSv12.Only();

    /// <inheritdoc />
    protected override int[] GetSupportedCipherSuites() =>
        TlsUtilities.GetSupportedCipherSuites(_crypto, EcdsaCipherSuites);

    /// <summary>
    /// Requires <c>extended_master_secret</c> — see <see cref="DtlsSrtpClient.RequiresExtendedMasterSecret"/>.
    /// </summary>
    public override bool RequiresExtendedMasterSecret() => true;

    /// <inheritdoc />
    public override void ProcessClientExtensions(IDictionary<int, byte[]>? clientExtensions)
    {
        // RFC 5764 §4.1: a DTLS-SRTP association is only established when the client
        // offered use_srtp and we share a profile. This server exists solely for SRTP
        // keying, so a plain-DTLS client (no extensions at all included) is a hard failure.
        var useSrtp = clientExtensions is null
            ? null
            : TlsSrtpUtilities.GetUseSrtpExtension(clientExtensions);
        if (useSrtp is null)
            throw new TlsFatalAlert(AlertDescription.handshake_failure);

        _selectedProfile = DtlsSrtpProfiles.SelectFromOffered(useSrtp.ProtectionProfiles)
            ?? throw new TlsFatalAlert(AlertDescription.insufficient_security);

        base.ProcessClientExtensions(clientExtensions);
    }

    /// <inheritdoc />
    public override IDictionary<int, byte[]> GetServerExtensions()
    {
        var extensions = base.GetServerExtensions() ?? new Dictionary<int, byte[]>();
        TlsSrtpUtilities.AddUseSrtpExtension(
            extensions, new UseSrtpData(new[] { _selectedProfile }, TlsUtilities.EmptyBytes));
        return extensions;
    }

    /// <inheritdoc />
    protected override TlsCredentialedSigner GetECDsaSignerCredentials() =>
        _localCertificate.CreateSignerCredentials(m_context);

    /// <inheritdoc />
    public override CertificateRequest GetCertificateRequest()
    {
        // Mutual authentication: both sides are verified by SDP fingerprint (RFC 5763 §5).
        var signatureAlgorithms = TlsUtilities.GetDefaultSupportedSignatureAlgorithms(m_context);
        return new CertificateRequest(
            new[] { ClientCertificateType.ecdsa_sign, ClientCertificateType.rsa_sign },
            signatureAlgorithms,
            certificateAuthorities: null);
    }

    /// <inheritdoc />
    public override void NotifyClientCertificate(Certificate clientCertificate) =>
        DtlsFingerprintValidator.Validate(clientCertificate, _expectedRemoteFingerprint);

    /// <inheritdoc />
    public override void NotifyHandshakeComplete()
    {
        base.NotifyHandshakeComplete();
        NegotiatedKeys = DtlsSrtpKeyExporter.Export(m_context, _selectedProfile, isClient: false);
    }
}
