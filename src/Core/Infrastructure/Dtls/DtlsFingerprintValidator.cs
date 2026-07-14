using Org.BouncyCastle.Tls;

namespace CalloraVoipSdk.Core.Infrastructure.Dtls;

/// <summary>
/// Verifies the peer's handshake certificate against the fingerprint signaled in SDP
/// (RFC 5763 §6.7.1 / RFC 8122). A mismatch is a hard failure: the handshake is aborted
/// with a fatal <c>bad_certificate</c> alert before any keying material is used.
/// </summary>
internal static class DtlsFingerprintValidator
{
    /// <summary>
    /// Validates the end-entity certificate of the peer's chain against the expected
    /// fingerprint. Only <c>sha-256</c> fingerprints are supported.
    /// </summary>
    /// <exception cref="TlsFatalAlert">
    /// <c>handshake_failure</c> when the chain is empty,
    /// <c>unsupported_certificate</c> for a non-SHA-256 fingerprint algorithm,
    /// <c>bad_certificate</c> on digest mismatch.
    /// </exception>
    public static void Validate(Certificate? peerCertificate, DtlsFingerprint expected)
    {
        ArgumentNullException.ThrowIfNull(expected);

        if (peerCertificate is null || peerCertificate.IsEmpty)
            throw new TlsFatalAlert(AlertDescription.handshake_failure);

        if (!string.Equals(expected.Algorithm, DtlsFingerprint.Sha256Algorithm, StringComparison.OrdinalIgnoreCase))
            throw new TlsFatalAlert(AlertDescription.unsupported_certificate);

        var endEntity = peerCertificate.GetCertificateAt(0);
        var actual = DtlsFingerprint.FromDerCertificate(endEntity.GetEncoded());

        if (!actual.Matches(expected))
            throw new TlsFatalAlert(AlertDescription.bad_certificate);
    }
}
