namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

/// <summary>
/// SRTP crypto suites as defined in RFC 4568 §6.2 and used in SDP a=crypto lines.
/// Each suite specifies the cipher, key length, authentication algorithm, and tag length.
/// </summary>
internal enum SrtpCryptoSuite
{
    /// <summary>
    /// AES-128 Counter Mode with HMAC-SHA1-80 (80-bit auth tag).
    /// Master key: 128 bit, master salt: 112 bit (RFC 3711 §4, RFC 4568 §6.2).
    /// Default suite for SDES.
    /// </summary>
    AesCm128HmacSha1_80,

    /// <summary>
    /// AES-128 Counter Mode with HMAC-SHA1-32 (32-bit auth tag).
    /// Reduced authentication overhead — suitable for low-bandwidth audio (RFC 4568 §6.2).
    /// </summary>
    AesCm128HmacSha1_32,

    /// <summary>
    /// AES-256 Counter Mode with HMAC-SHA1-80.
    /// 256-bit master key, 112-bit master salt (RFC 6188).
    /// </summary>
    AesCm256HmacSha1_80,

    /// <summary>
    /// AES-256 Counter Mode with HMAC-SHA1-32.
    /// 256-bit master key, 112-bit master salt (RFC 6188).
    /// </summary>
    AesCm256HmacSha1_32,

    /// <summary>
    /// AEAD AES-128 in Galois/Counter Mode (RFC 7714). 128-bit master key, <b>96-bit</b> master salt,
    /// intrinsic 128-bit authentication tag — no separate HMAC key. DTLS-SRTP protection profile 0x0007.
    /// </summary>
    AeadAes128Gcm,

    /// <summary>
    /// AEAD AES-256 in Galois/Counter Mode (RFC 7714). 256-bit master key, <b>96-bit</b> master salt,
    /// intrinsic 128-bit authentication tag. DTLS-SRTP protection profile 0x0008.
    /// </summary>
    AeadAes256Gcm,
}
