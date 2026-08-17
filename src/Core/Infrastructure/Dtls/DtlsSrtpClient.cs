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
    private readonly int _handshakeTimeoutMillis;
    private int _selectedProfile;

    public DtlsSrtpClient(
        TlsCrypto crypto,
        DtlsCertificate localCertificate,
        DtlsFingerprint expectedRemoteFingerprint,
        int handshakeTimeoutMillis)
        : base(crypto)
    {
        ArgumentNullException.ThrowIfNull(localCertificate);
        ArgumentNullException.ThrowIfNull(expectedRemoteFingerprint);
        _localCertificate = localCertificate;
        _expectedRemoteFingerprint = expectedRemoteFingerprint;
        _handshakeTimeoutMillis = handshakeTimeoutMillis;
    }

    /// <summary>SRTP keys exported after <see cref="NotifyHandshakeComplete"/>.</summary>
    public DtlsSrtpNegotiatedKeys? NegotiatedKeys { get; private set; }

    /// <inheritdoc />
    // AEAD only (#323, follow-up to #229). BouncyCastle's DefaultTlsClient advertises a broad legacy list —
    // measured off the wire, our ClientHello carried eight AES-CBC suites, four of them with SHA-1, plus
    // static-RSA key exchange without forward secrecy. None of it is reachable in practice (the peer picks,
    // and every WebRTC endpoint picks AEAD), but Anlage 31b BMV-Ä has the handshake assessed against BSI
    // TR-02102, and what is not offered needs no defending.
    //
    // Filtered, not replaced: the base list is kept minus the CBC suites, so the RSA and DHE families survive.
    // Pinning the three ECDHE_ECDSA suites of the server side here instead would refuse every peer with an
    // RSA certificate, which RFC 8827 explicitly permits — a hardening that breaks interop is not one.
    protected override int[] GetSupportedCipherSuites() =>
        [.. base.GetSupportedCipherSuites().Where(suite => !IsCbc(suite))];

    // The AES-CBC suites of the TLS registry that BouncyCastle's default list can contribute (RFC 5246 §A.5,
    // RFC 5289, RFC 8422). Matched by value: the constants live in several BouncyCastle enums, and a literal
    // table is what the wire test asserts against.
    private static bool IsCbc(int cipherSuite) => cipherSuite is
        0xC023 or 0xC024 or 0xC009 or 0xC00A or   // ECDHE_ECDSA
        0xC027 or 0xC028 or 0xC013 or 0xC014 or   // ECDHE_RSA
        0x0067 or 0x006B or 0x0033 or 0x0039 or   // DHE_RSA
        0x003C or 0x003D or 0x002F or 0x0035;     // RSA

    protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.DTLSv12.Only();

    /// <summary>
    /// Overrides the BouncyCastle default (no overall handshake deadline) with a finite ceiling
    /// so the engine aborts a stalled handshake on its own — defence-in-depth below the
    /// transport-close failsafe in <see cref="DtlsSrtpHandshaker"/> (#163 P1-1).
    /// </summary>
    public override int GetHandshakeTimeoutMillis() => _handshakeTimeoutMillis;

    /// <summary>Bounds per-handshake reassembly memory (rule K4). See <see cref="DtlsHandshakeLimits"/>.</summary>
    public override int GetMaxHandshakeMessageSize() => DtlsHandshakeLimits.MaxHandshakeMessageSize;

    /// <summary>Bounds the accepted server certificate chain (rule K4). See <see cref="DtlsHandshakeLimits"/>.</summary>
    public override int GetMaxCertificateChainLength() => DtlsHandshakeLimits.MaxCertificateChainLength;

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
            throw new TlsFatalAlert(
                AlertDescription.illegal_parameter,
                DtlsSrtpProfiles.FormatNoCommonProfileError(new[] { profile }));

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
