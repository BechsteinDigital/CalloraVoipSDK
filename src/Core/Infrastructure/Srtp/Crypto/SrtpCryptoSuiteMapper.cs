using System;
using CalloraVoipSdk.Core.Security;

namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

/// <summary>
/// Maps SDES crypto-suite tokens (RFC 4568 §6.2, RFC 6188) to the infrastructure
/// <see cref="SrtpCryptoSuite"/> enum and the domain-neutral <see cref="SrtpCryptoSuiteKind"/>.
/// Accepts the common RFC-4568 (AES_CM_128_*) and RFC-6188 (AES_256_CM_*) spellings,
/// plus the frequently seen AES_CM_256_* variant.
/// </summary>
internal static class SrtpCryptoSuiteMapper
{
    /// <summary>Master salt length for every currently supported suite (RFC 3711 §3.2.1).</summary>
    public const int MasterSaltLength = 14;

    /// <summary>
    /// Parses an SDES crypto-suite name into the infrastructure enum.
    /// Returns <see langword="false"/> for null, empty, or unsupported suite names.
    /// </summary>
    public static bool TryParseSuiteName(string? suiteName, out SrtpCryptoSuite suite)
    {
        suite = default;
        if (string.IsNullOrWhiteSpace(suiteName))
            return false;

        switch (suiteName.Trim().ToUpperInvariant())
        {
            case "AES_CM_128_HMAC_SHA1_80":
                suite = SrtpCryptoSuite.AesCm128HmacSha1_80;
                return true;
            case "AES_CM_128_HMAC_SHA1_32":
                suite = SrtpCryptoSuite.AesCm128HmacSha1_32;
                return true;
            case "AES_256_CM_HMAC_SHA1_80":
            case "AES_CM_256_HMAC_SHA1_80":
                suite = SrtpCryptoSuite.AesCm256HmacSha1_80;
                return true;
            case "AES_256_CM_HMAC_SHA1_32":
            case "AES_CM_256_HMAC_SHA1_32":
                suite = SrtpCryptoSuite.AesCm256HmacSha1_32;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Projects the infrastructure suite onto the domain-neutral suite kind.
    /// </summary>
    public static SrtpCryptoSuiteKind ToDomainKind(SrtpCryptoSuite suite) => suite switch
    {
        SrtpCryptoSuite.AesCm128HmacSha1_80 => SrtpCryptoSuiteKind.AesCm128HmacSha1_80,
        SrtpCryptoSuite.AesCm128HmacSha1_32 => SrtpCryptoSuiteKind.AesCm128HmacSha1_32,
        SrtpCryptoSuite.AesCm256HmacSha1_80 => SrtpCryptoSuiteKind.AesCm256HmacSha1_80,
        SrtpCryptoSuite.AesCm256HmacSha1_32 => SrtpCryptoSuiteKind.AesCm256HmacSha1_32,
        _ => throw new ArgumentOutOfRangeException(nameof(suite), suite, "Unknown SRTP crypto suite."),
    };

    /// <summary>
    /// Projects the domain-neutral suite kind onto the infrastructure suite so negotiated
    /// SDES key material can drive the SRTP crypto context.
    /// </summary>
    public static SrtpCryptoSuite FromDomainKind(SrtpCryptoSuiteKind kind) => kind switch
    {
        SrtpCryptoSuiteKind.AesCm128HmacSha1_80 => SrtpCryptoSuite.AesCm128HmacSha1_80,
        SrtpCryptoSuiteKind.AesCm128HmacSha1_32 => SrtpCryptoSuite.AesCm128HmacSha1_32,
        SrtpCryptoSuiteKind.AesCm256HmacSha1_80 => SrtpCryptoSuite.AesCm256HmacSha1_80,
        SrtpCryptoSuiteKind.AesCm256HmacSha1_32 => SrtpCryptoSuite.AesCm256HmacSha1_32,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown SRTP crypto suite kind."),
    };

    /// <summary>
    /// Returns the master key length in bytes for the suite:
    /// 16 bytes for AES-128 suites, 32 bytes for AES-256 suites (RFC 3711 §4, RFC 6188).
    /// </summary>
    public static int GetMasterKeyLength(SrtpCryptoSuite suite) => suite switch
    {
        SrtpCryptoSuite.AesCm256HmacSha1_80 or SrtpCryptoSuite.AesCm256HmacSha1_32 => 32,
        _ => 16,
    };
}
