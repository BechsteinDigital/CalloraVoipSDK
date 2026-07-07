using System.Diagnostics.CodeAnalysis;

namespace CalloraVoipSdk.Core.Security;

/// <summary>
/// Domain-neutral mirror of the SRTP crypto suites used for SDES key management
/// (RFC 4568 §6.2, RFC 6188). This enum lets the domain layer carry the negotiated
/// suite without depending on any infrastructure crypto type.
/// </summary>
[SuppressMessage(
    "Naming", "CA1707:Identifiers should not contain underscores",
    Justification = "Members deliberately mirror the RFC 4568/6188 crypto-suite tokens "
        + "(AES_CM_128_HMAC_SHA1_80 etc.) for unambiguous mapping.")]
public enum SrtpCryptoSuiteKind
{
    /// <summary>
    /// AES-128 Counter Mode with HMAC-SHA1-80 (80-bit auth tag).
    /// Master key: 128 bit, master salt: 112 bit. Default SDES suite.
    /// </summary>
    AesCm128HmacSha1_80,

    /// <summary>
    /// AES-128 Counter Mode with HMAC-SHA1-32 (32-bit auth tag).
    /// Master key: 128 bit, master salt: 112 bit.
    /// </summary>
    AesCm128HmacSha1_32,

    /// <summary>
    /// AES-256 Counter Mode with HMAC-SHA1-80 (80-bit auth tag).
    /// Master key: 256 bit, master salt: 112 bit (RFC 6188).
    /// </summary>
    AesCm256HmacSha1_80,

    /// <summary>
    /// AES-256 Counter Mode with HMAC-SHA1-32 (32-bit auth tag).
    /// Master key: 256 bit, master salt: 112 bit (RFC 6188).
    /// </summary>
    AesCm256HmacSha1_32,
}
