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
    /// Compares against another fingerprint. The algorithm token is a public RFC 8122 §5 label compared
    /// case-insensitively (it also fixes the digest length). The hex digest — the only credential binding the
    /// DTLS connection to the signaled identity — is parsed to fixed bytes and compared in constant time
    /// (ENGINEERING_RULES K5): a differing length or a malformed digest is not secret and may short-circuit,
    /// but two equal-length digests are compared without leaking where the first differing byte is.
    /// </summary>
    public bool Matches(DtlsFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (!string.Equals(Algorithm, other.Algorithm, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!TryParseDigest(Value, out var thisDigest) || !TryParseDigest(other.Value, out var otherDigest))
            return false;

        // FixedTimeEquals requires equal-length inputs; the length itself is public (fixed by the algorithm).
        if (thisDigest.Length != otherDigest.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(thisDigest, otherDigest);
    }

    /// <summary>
    /// Parses an RFC 8122 §5 colon-delimited hex digest (e.g. <c>AB:CD:…</c>) into its raw bytes. Returns
    /// <see langword="false"/> for any malformed input (wrong length, missing separators, non-hex characters)
    /// so a comparison against it fails closed rather than throwing.
    /// </summary>
    private static bool TryParseDigest(string value, out byte[] digest)
    {
        digest = [];
        if (string.IsNullOrEmpty(value))
            return false;

        // Each byte is two hex chars joined by a single ':', so a k-byte digest is exactly 3*k - 1 chars long.
        if ((value.Length + 1) % 3 != 0)
            return false;

        var count = (value.Length + 1) / 3;
        var buffer = new byte[count];
        for (var i = 0; i < count; i++)
        {
            var pos = i * 3;
            if (i > 0 && value[pos - 1] != ':')
                return false;
            if (!byte.TryParse(
                    value.AsSpan(pos, 2),
                    System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out buffer[i]))
                return false;
        }

        digest = buffer;
        return true;
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
