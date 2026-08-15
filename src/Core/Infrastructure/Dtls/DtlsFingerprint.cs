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
    /// The hash function this SDK emits. SHA-256 is the de-facto WebRTC standard and RFC 8122 §5 requires
    /// every implementation to support it, so an offer carrying it is understood everywhere.
    /// </summary>
    public const string Sha256Algorithm = "sha-256";

    /// <summary>
    /// Hash functions accepted when <em>verifying</em> a peer's fingerprint, from the RFC 8122 §5 registry.
    /// </summary>
    /// <remarks>
    /// We emit only SHA-256 but must verify whatever the peer signalled, because the fingerprint is checked
    /// against the certificate the peer actually presented — with the peer's chosen hash, not ours.
    /// <para>
    /// <c>md2</c>, <c>md5</c> and <c>sha-1</c> are deliberately absent although the registry lists them —
    /// it dates back to RFC 4572 (2006). A fingerprint breaks on a <em>collision</em>, not on a preimage:
    /// an attacker needs no luck against someone else's certificate, they mint two of their own with the
    /// same digest, signal the fingerprint of one and present the other. That is exactly the 2008 rogue-CA
    /// attack on MD5, and this fingerprint is the <em>only</em> binding between the signalled identity and
    /// the DTLS endpoint (RFC 8122 §6). RFC 8122 §5 requires SHA-256 support and nothing weaker.
    /// </para>
    /// <para>
    /// This is stricter than all three reference stacks, which is a deliberate call rather than an
    /// oversight. libwebrtc gates on <c>IsFips180DigestAlgorithm</c> — SHA-1 through SHA-512, MD5 refused.
    /// pjsip supports exactly SHA-256 and SHA-1. SIPSorcery filters nothing at all: its
    /// <c>IsHashSupported</c> asks BouncyCastle whether it can build the digest, which is a capability
    /// check where a policy check belongs, so MD2 and MD5 pass.
    /// </para>
    /// <para>
    /// Dropping SHA-1 costs interoperability with a peer configured for it — pjsip can be. The price is
    /// judged low (browsers and current stacks signal SHA-256) against the alternative: suppressing CA5350
    /// to compute a hash we would not want to trust anyway. Such a peer fails the handshake with
    /// <c>unsupported_certificate</c> instead of silently receiving a weaker binding.
    /// </para>
    /// </remarks>
    private static readonly string[] SupportedAlgorithms =
        ["sha-224", "sha-256", "sha-384", "sha-512"];

    /// <summary>
    /// Whether this SDK can verify a fingerprint carrying <paramref name="algorithm"/>.
    /// </summary>
    public static bool IsSupportedAlgorithm(string? algorithm) =>
        algorithm is not null
        && SupportedAlgorithms.Contains(algorithm.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Computes the <c>sha-256</c> fingerprint of a DER-encoded certificate in
    /// RFC 8122 §5 format (upper-case hex, colon-delimited).
    /// </summary>
    public static DtlsFingerprint FromDerCertificate(ReadOnlySpan<byte> derEncodedCertificate) =>
        FromDerCertificate(derEncodedCertificate, Sha256Algorithm);

    /// <summary>
    /// Computes the fingerprint of a DER-encoded certificate under a specific hash function, in
    /// RFC 8122 §5 format (upper-case hex, colon-delimited).
    /// </summary>
    /// <param name="derEncodedCertificate">The DER encoding of the certificate to digest.</param>
    /// <param name="algorithm">
    /// An RFC 8122 §5 hash token accepted by <see cref="IsSupportedAlgorithm"/>.
    /// </param>
    /// <exception cref="ArgumentException">The algorithm is unknown or refused.</exception>
    public static DtlsFingerprint FromDerCertificate(ReadOnlySpan<byte> derEncodedCertificate, string algorithm)
    {
        if (!IsSupportedAlgorithm(algorithm))
            throw new ArgumentException($"Unsupported fingerprint hash function '{algorithm}'.", nameof(algorithm));

        var normalised = algorithm.Trim().ToLowerInvariant();
        return new DtlsFingerprint
        {
            Algorithm = normalised,
            Value = FormatDigest(ComputeDigest(derEncodedCertificate, normalised)),
        };
    }

    private static byte[] ComputeDigest(ReadOnlySpan<byte> data, string algorithm) => algorithm switch
    {
        // .NET has no SHA-224 primitive; BouncyCastle is already a DTLS dependency, so the registry entry
        // costs one digest instance rather than an exception the peer cannot act on.
        "sha-224" => Sha224(data),
        "sha-256" => SHA256.HashData(data),
        "sha-384" => SHA384.HashData(data),
        "sha-512" => SHA512.HashData(data),
        _ => throw new ArgumentException($"Unsupported fingerprint hash function '{algorithm}'.", nameof(algorithm)),
    };

    private static byte[] Sha224(ReadOnlySpan<byte> data)
    {
        var digest = new Org.BouncyCastle.Crypto.Digests.Sha224Digest();
        digest.BlockUpdate(data);
        var output = new byte[digest.GetDigestSize()];
        digest.DoFinal(output);
        return output;
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
