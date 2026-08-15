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
    /// Validates the end-entity certificate of the peer's chain against the expected fingerprint, digesting
    /// the certificate with the hash function the peer signalled (RFC 8122 §5) rather than assuming SHA-256.
    /// </summary>
    /// <remarks>
    /// The hash is the peer's choice, not ours: we only ever offer SHA-256, but a peer may verify us with one
    /// hash while presenting its own fingerprint under another. Computing the digest with the algorithm named
    /// in the offer is what makes the comparison meaningful — hashing with SHA-256 regardless would simply
    /// mismatch, which is why every non-SHA-256 peer used to fail the handshake.
    /// See <see cref="DtlsFingerprint.IsSupportedAlgorithm"/> for which functions are accepted and why MD5 and
    /// MD2 are not.
    /// </remarks>
    /// <exception cref="TlsFatalAlert">
    /// <c>handshake_failure</c> when the chain is empty,
    /// <c>unsupported_certificate</c> for a hash function this SDK refuses,
    /// <c>bad_certificate</c> on digest mismatch.
    /// </exception>
    public static void Validate(Certificate? peerCertificate, DtlsFingerprint expected)
    {
        ArgumentNullException.ThrowIfNull(expected);

        if (peerCertificate is null || peerCertificate.IsEmpty)
            throw new TlsFatalAlert(AlertDescription.handshake_failure);

        if (!DtlsFingerprint.IsSupportedAlgorithm(expected.Algorithm))
            throw new TlsFatalAlert(AlertDescription.unsupported_certificate);

        var endEntity = peerCertificate.GetCertificateAt(0);
        var actual = DtlsFingerprint.FromDerCertificate(endEntity.GetEncoded(), expected.Algorithm);

        if (!actual.Matches(expected))
            throw new TlsFatalAlert(AlertDescription.bad_certificate);
    }
}
