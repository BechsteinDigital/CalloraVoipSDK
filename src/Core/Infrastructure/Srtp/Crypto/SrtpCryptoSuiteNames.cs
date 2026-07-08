namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

/// <summary>
/// Maps RFC 4568/6188 crypto-suite tokens to the implemented <see cref="SrtpCryptoSuite"/>
/// values and exposes the per-suite master key/salt lengths (RFC 3711 §3.2.1).
/// Single source of truth shared by SDP negotiation and media-path key parsing.
/// </summary>
internal static class SrtpCryptoSuiteNames
{
    /// <summary>Master salt length in bytes — 112 bit for all implemented suites.</summary>
    public const int SaltLength = 14;

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

    /// <summary>Master key length in bytes for one suite (16 for AES-128, 32 for AES-256).</summary>
    public static int KeyLength(SrtpCryptoSuite suite) =>
        suite is SrtpCryptoSuite.AesCm256HmacSha1_80 or SrtpCryptoSuite.AesCm256HmacSha1_32
            ? 32 : 16;
}
