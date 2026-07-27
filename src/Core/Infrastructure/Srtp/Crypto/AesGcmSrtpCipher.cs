using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

/// <summary>
/// AEAD AES-GCM cipher for SRTP (RFC 7714 §8). Encrypts the RTP payload and authenticates the clear-text
/// RTP header (passed as AAD) under a 96-bit per-packet IV built from SSRC, rollover counter and sequence
/// number (§8.1). One instance per crypto context, holding the derived GCM key schedule and the 12-byte
/// session salt; the owning context serialises every call under its own lock, so the reused key schedule
/// is not shared across threads. Encryption and decryption produce/verify a full 16-octet tag — RFC 7714
/// §14 forbids truncation.
/// </summary>
internal sealed class AesGcmSrtpCipher : IDisposable
{
    /// <summary>The AEAD tag is always the full 16 octets (RFC 7714 §14 — never truncated).</summary>
    public const int TagLength = 16;

    private const int IvLength = 12;

    private readonly AesGcm _gcm;
    private readonly byte[] _salt; // 12-byte session salt (RFC 7714 §8.1); owned by the session keys.

    /// <param name="keys">
    /// Derived session keys for an AEAD-GCM suite: <see cref="SrtpSessionKeys.CipherKey"/> (16 or 32 bytes)
    /// and a 12-byte <see cref="SrtpSessionKeys.Salt"/>. <see cref="SrtpSessionKeys.AuthKey"/> is unused.
    /// </param>
    public AesGcmSrtpCipher(SrtpSessionKeys keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Salt.Length != IvLength)
            throw new ArgumentException(
                $"AEAD-GCM requires a {IvLength}-byte session salt (RFC 7714 §8.1), got {keys.Salt.Length}.", nameof(keys));

        _gcm = new AesGcm(keys.CipherKey, TagLength);
        _salt = keys.Salt;
    }

    /// <summary>
    /// Encrypts <paramref name="payload"/> into <paramref name="ciphertext"/> and writes the 16-byte
    /// <paramref name="tag"/>, authenticating <paramref name="header"/> (the clear-text RTP header, incl.
    /// CSRC list and any header extension) as AAD. The IV is derived from <paramref name="ssrc"/>,
    /// <paramref name="rolloverCounter"/> and <paramref name="sequenceNumber"/> per RFC 7714 §8.1.
    /// </summary>
    public void Encrypt(
        uint ssrc, uint rolloverCounter, ushort sequenceNumber,
        ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload, Span<byte> ciphertext, Span<byte> tag)
    {
        Span<byte> iv = stackalloc byte[IvLength];
        BuildIv(ssrc, rolloverCounter, sequenceNumber, iv);
        _gcm.Encrypt(iv, payload, ciphertext, tag, header);
    }

    /// <summary>
    /// Verifies <paramref name="tag"/> over <paramref name="header"/> (AAD) + <paramref name="ciphertext"/>
    /// and decrypts into <paramref name="plaintext"/>. Throws <see cref="AuthenticationTagMismatchException"/>
    /// when the tag does not verify (RFC 7714 §8 — reject before releasing plaintext).
    /// </summary>
    public void Decrypt(
        uint ssrc, uint rolloverCounter, ushort sequenceNumber,
        ReadOnlySpan<byte> header, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, Span<byte> plaintext)
    {
        Span<byte> iv = stackalloc byte[IvLength];
        BuildIv(ssrc, rolloverCounter, sequenceNumber, iv);
        _gcm.Decrypt(iv, ciphertext, tag, plaintext, header);
    }

    // RFC 7714 §8.1: pre-IV = 0x0000 || SSRC(4) || ROC(4) || SEQ(2) (big-endian, 12 octets), then XOR salt.
    private void BuildIv(uint ssrc, uint rolloverCounter, ushort sequenceNumber, Span<byte> iv)
    {
        iv.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(iv[2..], ssrc);            // bytes 2..5
        BinaryPrimitives.WriteUInt32BigEndian(iv[6..], rolloverCounter); // bytes 6..9
        BinaryPrimitives.WriteUInt16BigEndian(iv[10..], sequenceNumber); // bytes 10..11
        for (var i = 0; i < IvLength; i++)
            iv[i] ^= _salt[i];
    }

    public void Dispose() => _gcm.Dispose();
}
