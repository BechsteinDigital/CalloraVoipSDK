namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

/// <summary>
/// Maps RFC 4568/6188 crypto-suite tokens to the implemented <see cref="SrtpCryptoSuite"/>
/// values and exposes the per-suite master key/salt lengths (RFC 3711 §3.2.1).
/// Single source of truth shared by SDP negotiation and media-path key parsing.
/// </summary>
internal static class SrtpCryptoSuiteNames
{
    /// <summary>
    /// Master/session salt length in bytes: 14 (112 bit) for the AES-CM suites, 12 (96 bit) for the
    /// AEAD-GCM suites (RFC 7714 §8.1). The DTLS key export and the KDF both key off this per suite.
    /// </summary>
    public static int SaltLength(SrtpCryptoSuite suite) => IsAead(suite) ? 12 : 14;

    /// <summary>
    /// Whether the suite is an AEAD (AES-GCM) suite: no separate HMAC auth key, 12-byte salt, and an
    /// intrinsic 16-byte authentication tag (RFC 7714). The AES-CM suites return <see langword="false"/>.
    /// </summary>
    public static bool IsAead(SrtpCryptoSuite suite) =>
        suite is SrtpCryptoSuite.AeadAes128Gcm or SrtpCryptoSuite.AeadAes256Gcm;

    /// <summary>
    /// Mandatory-to-implement default suite token (RFC 4568 §6.2) offered when a locally
    /// originated SDES offer needs a single unambiguous crypto line.
    /// </summary>
    public const string DefaultSuiteName = "AES_CM_128_HMAC_SHA1_80";

    /// <summary>
    /// Parses a suite token (case-sensitive per RFC 4568 grammar) to the implemented suite.
    /// Returns <see langword="null"/> for unknown/unsupported suites.
    /// </summary>
    public static SrtpCryptoSuite? TryParse(string suiteName) => suiteName switch
    {
        "AES_CM_128_HMAC_SHA1_80" => SrtpCryptoSuite.AesCm128HmacSha1_80,
        "AES_CM_128_HMAC_SHA1_32" => SrtpCryptoSuite.AesCm128HmacSha1_32,
        "AES_256_CM_HMAC_SHA1_80" => SrtpCryptoSuite.AesCm256HmacSha1_80,
        "AES_256_CM_HMAC_SHA1_32" => SrtpCryptoSuite.AesCm256HmacSha1_32,
        _ => null
    };

    /// <summary>Master key length in bytes for one suite (16 for AES-128/GCM-128, 32 for AES-256/GCM-256).</summary>
    public static int KeyLength(SrtpCryptoSuite suite) => suite switch
    {
        SrtpCryptoSuite.AesCm256HmacSha1_80
            or SrtpCryptoSuite.AesCm256HmacSha1_32
            or SrtpCryptoSuite.AeadAes256Gcm => 32,
        _ => 16,
    };
}
