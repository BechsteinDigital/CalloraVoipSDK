using Org.BouncyCastle.Tls;

namespace CalloraVoipSdk.Core.Infrastructure.Dtls;

/// <summary>
/// Client-side certificate handling for the DTLS-SRTP handshake: validates the server
/// certificate against the SDP-signaled fingerprint the moment it arrives (RFC 5763
/// §6.7.1 — abort before any key material is used) and answers the server's certificate
/// request with the local self-signed identity (WebRTC handshakes are mutually
/// authenticated by fingerprint).
/// </summary>
internal sealed class DtlsSrtpClientAuthentication : TlsAuthentication
{
    private readonly TlsContext _context;
    private readonly DtlsCertificate _localCertificate;
    private readonly DtlsFingerprint _expectedRemoteFingerprint;

    public DtlsSrtpClientAuthentication(
        TlsContext context,
        DtlsCertificate localCertificate,
        DtlsFingerprint expectedRemoteFingerprint)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(localCertificate);
        ArgumentNullException.ThrowIfNull(expectedRemoteFingerprint);
        _context = context;
        _localCertificate = localCertificate;
        _expectedRemoteFingerprint = expectedRemoteFingerprint;
    }

    /// <inheritdoc />
    public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
    {
        ArgumentNullException.ThrowIfNull(serverCertificate);
        DtlsFingerprintValidator.Validate(serverCertificate.Certificate, _expectedRemoteFingerprint);
    }

    /// <inheritdoc />
    public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest) =>
        _localCertificate.CreateSignerCredentials(_context);
}
