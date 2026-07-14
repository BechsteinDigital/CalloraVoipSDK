using System.Security.Cryptography;

namespace CalloraVoipSdk.Core.Infrastructure.Dtls;

/// <summary>
/// Certificate fingerprint as conveyed in the SDP <c>a=fingerprint</c> attribute
/// (RFC 8122): a hash-function token plus the colon-delimited upper-case hex digest
/// of the DER-encoded certificate. Kept as a DTLS-module value type so the DTLS
/// layer does not depend on SDP model types; the signaling layer converts.
/// </summary>
internal sealed record DtlsFingerprint
{
    /// <summary>Hash function token per RFC 8122 §5, e.g. <c>sha-256</c>.</summary>
    public required string Algorithm { get; init; }

    /// <summary>Colon-delimited hex digest, e.g. <c>AB:CD:…</c>. Compared case-insensitively.</summary>
    public required string Value { get; init; }

    /// <summary>
    /// The only hash function this SDK emits and verifies. SHA-256 is the de-facto
    /// WebRTC standard; RFC 8122 §5 recommends it for new applications.
    /// </summary>
    public const string Sha256Algorithm = "sha-256";

    /// <summary>
    /// Computes the <c>sha-256</c> fingerprint of a DER-encoded certificate in
    /// RFC 8122 §5 format (upper-case hex, colon-delimited).
    /// </summary>
    public static DtlsFingerprint FromDerCertificate(ReadOnlySpan<byte> derEncodedCertificate)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(derEncodedCertificate, digest);
        return new DtlsFingerprint
        {
            Algorithm = Sha256Algorithm,
            Value = FormatDigest(digest),
        };
    }

    /// <summary>
    /// Compares against another fingerprint: algorithm token and hex digest are both
    /// case-insensitive per RFC 8122 §5 grammar (hex digits may be either case).
    /// </summary>
    public bool Matches(DtlsFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(Algorithm, other.Algorithm, StringComparison.OrdinalIgnoreCase)
               && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatDigest(ReadOnlySpan<byte> digest)
    {
        // "AB:CD:…" — 3 chars per byte minus the trailing separator.
        return string.Create(digest.Length * 3 - 1, digest.ToArray(), static (span, bytes) =>
        {
            var pos = 0;
            for (var i = 0; i < bytes.Length; i++)
            {
                if (i > 0)
                    span[pos++] = ':';
                bytes[i].TryFormat(span[pos..], out _, "X2");
                pos += 2;
            }
        });
    }
}
