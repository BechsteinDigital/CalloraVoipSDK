using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;

namespace CalloraVoipSdk.Core.Infrastructure.Dtls;

/// <summary>
/// DTLS 1.2 client for DTLS-SRTP (RFC 5764): offers the <c>use_srtp</c> extension with
/// the SDK's supported protection profiles, requires the server to mirror exactly one of
/// them, verifies the server certificate against the SDP-signaled fingerprint
/// (RFC 5763 §6.7.1), and exports the SRTP master keys when the handshake completes.
/// </summary>
internal sealed class DtlsSrtpClient : DefaultTlsClient
{
    private readonly DtlsCertificate _localCertificate;
    private readonly DtlsFingerprint _expectedRemoteFingerprint;
    private int _selectedProfile;

    public DtlsSrtpClient(
        TlsCrypto crypto,
        DtlsCertificate localCertificate,
        DtlsFingerprint expectedRemoteFingerprint)
        : base(crypto)
    {
        ArgumentNullException.ThrowIfNull(localCertificate);
        ArgumentNullException.ThrowIfNull(expectedRemoteFingerprint);
        _localCertificate = localCertificate;
        _expectedRemoteFingerprint = expectedRemoteFingerprint;
    }

    /// <summary>SRTP keys exported after <see cref="NotifyHandshakeComplete"/>.</summary>
    public DtlsSrtpNegotiatedKeys? NegotiatedKeys { get; private set; }

    /// <inheritdoc />
    protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.DTLSv12.Only();

    /// <summary>
    /// Requires <c>extended_master_secret</c>: RFC 5764 keying-material export must bind
    /// to the full handshake transcript (triple-handshake hardening), and the BouncyCastle
    /// exporter refuses to run without it.
    /// </summary>
    public override bool RequiresExtendedMasterSecret() => true;

    /// <inheritdoc />
    public override IDictionary<int, byte[]> GetClientExtensions()
    {
        var extensions = base.GetClientExtensions() ?? new Dictionary<int, byte[]>();
        TlsSrtpUtilities.AddUseSrtpExtension(
            extensions, new UseSrtpData(DtlsSrtpProfiles.Supported, TlsUtilities.EmptyBytes));
        return extensions;
    }

    /// <inheritdoc />
    public override void ProcessServerExtensions(IDictionary<int, byte[]>? serverExtensions)
    {
        // RFC 5764 §4.1: the server MUST mirror use_srtp with exactly one profile taken
        // from our offer. Anything else means the peer does not speak DTLS-SRTP — abort
        // before certificates or keys are touched.
        var useSrtp = serverExtensions is null ? null : TlsSrtpUtilities.GetUseSrtpExtension(serverExtensions);
        if (useSrtp is null || useSrtp.ProtectionProfiles.Length != 1)
            throw new TlsFatalAlert(AlertDescription.handshake_failure);

        var profile = useSrtp.ProtectionProfiles[0];
        if (Array.IndexOf(DtlsSrtpProfiles.Supported, profile) < 0)
            throw new TlsFatalAlert(AlertDescription.illegal_parameter);

        // RFC 5764 §4.1.3: the server's srtp_mki MUST match the client's offer — we
        // offered an empty MKI, so any non-empty echo is a protocol violation.
        if (useSrtp.Mki is { Length: > 0 })
            throw new TlsFatalAlert(AlertDescription.illegal_parameter);

        _selectedProfile = profile;
        base.ProcessServerExtensions(serverExtensions);
    }

    /// <inheritdoc />
    public override TlsAuthentication GetAuthentication() =>
        new DtlsSrtpClientAuthentication(m_context, _localCertificate, _expectedRemoteFingerprint);

    /// <inheritdoc />
    public override void NotifyHandshakeComplete()
    {
        base.NotifyHandshakeComplete();
        NegotiatedKeys = DtlsSrtpKeyExporter.Export(m_context, _selectedProfile, isClient: true);
    }
}
