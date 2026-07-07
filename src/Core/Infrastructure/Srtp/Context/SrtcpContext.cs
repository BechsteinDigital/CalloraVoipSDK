using System.Buffers.Binary;
using System.Security.Cryptography;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Context;

/// <summary>
/// SRTCP context for one direction of the RTCP control path (RFC 3711 §3.4).
/// Implements AES-CM encryption (§4.1.1), mandatory HMAC-SHA1 authentication (§4.2),
/// and replay protection over the 31-bit SRTCP index via a 64-packet sliding window (§3.3.2).
/// </summary>
/// <remarks>
/// Thread-safe by design and modelled exactly on <see cref="SrtpContext"/>:
/// <see cref="Protect"/> (send pump) and <see cref="Unprotect"/> (receive pump) may run
/// concurrently on one instance. Each direction owns a dedicated lock, a cached AES-CM
/// encryptor and reusable scratch buffers, so send and receive never contend and never
/// corrupt each other's state. The cached <see cref="ICryptoTransform"/> instances are not
/// thread-safe themselves; each is only ever touched under its own lock. Call
/// <see cref="Dispose"/> to release them.
/// </remarks>
internal sealed class SrtcpContext : ISrtcpContext, IDisposable
{
    private const int AuthTagFullLength = 20;
    private const int AesCmBlockLength = 16;
    private const int MaxAesCmKeystreamBytes = 1 << 20;

    // The first 8 bytes of an RTCP compound packet (fixed header + sender SSRC) stay
    // cleartext; encryption covers everything from offset 8 onward (RFC 3711 §3.4).
    private const int CleartextHeaderLength = 8;

    // Trailing 32-bit word: high bit = E (encryption) flag, low 31 bits = SRTCP index.
    private const int SrtcpIndexLength = 4;
    private const uint EncryptionFlag = 0x8000_0000u;
    private const uint IndexMask = 0x7FFF_FFFFu;

    private const int ReplayWindowSize = 64;

    private readonly SrtpSessionKeys _keys;
    private readonly SrtpCryptoSuite _suite;
    private readonly int _authTagLength;

    // Per-direction synchronization. Protect (send pump) and Unprotect (receive pump)
    // mutate disjoint state and use disjoint crypto resources, so separate locks let both
    // run in parallel without contention.
    private readonly object _protectSync = new();
    private readonly object _unprotectSync = new();

    private readonly Aes _protectAes;
    private readonly Aes _unprotectAes;
    private readonly ICryptoTransform _protectEncryptor;
    private readonly ICryptoTransform _unprotectEncryptor;

    private readonly byte[] _protectCounterIv = new byte[AesCmBlockLength];
    private readonly byte[] _protectBlock = new byte[AesCmBlockLength];
    private readonly byte[] _unprotectCounterIv = new byte[AesCmBlockLength];
    private readonly byte[] _unprotectBlock = new byte[AesCmBlockLength];

    // Sender-side SRTCP index (31-bit), starts at 0 and increments once per Protect.
    // Guarded by _protectSync.
    private uint _sendIndex;

    // Receiver replay window over the SRTCP index (RFC 3711 §3.3.2). The 31-bit index is
    // carried in full on the wire, so no seq-based extension is needed. Guarded by
    // _unprotectSync. The bitmap high bit tracks the newest accepted index.
    private uint _replayWindowIndex;
    private ulong _replayWindowBitmap;

    public SrtcpContext(SrtpKeyMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        _suite         = material.Suite;
        _keys          = SrtpKeyDerivation.DeriveSrtcp(material);
        _authTagLength = material.Suite is SrtpCryptoSuite.AesCm128HmacSha1_32
                                       or SrtpCryptoSuite.AesCm256HmacSha1_32
            ? 4 : 10;

        _protectAes         = CreateAesCmCipher(_keys.CipherKey);
        _unprotectAes       = CreateAesCmCipher(_keys.CipherKey);
        _protectEncryptor   = _protectAes.CreateEncryptor();
        _unprotectEncryptor = _unprotectAes.CreateEncryptor();
    }

    private static Aes CreateAesCmCipher(byte[] cipherKey)
    {
        var aes     = Aes.Create();
        aes.Key     = cipherKey;
        aes.Mode    = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        return aes;
    }

    // -------------------------------------------------------------------------
    // Protect (encrypt + authenticate outbound RTCP)
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> rtcpPacket)
    {
        if (rtcpPacket.Length < CleartextHeaderLength)
            throw new ArgumentException(
                "RTCP packet too short (minimum 8 bytes for header + sender SSRC).",
                nameof(rtcpPacket));

        // SSRC of the first RTCP header (bytes 4..7) seeds the AES-CM IV (RFC 3711 §4.1.1).
        var ssrc        = BinaryPrimitives.ReadUInt32BigEndian(rtcpPacket[4..]);
        var encryptedLen = rtcpPacket.Length - CleartextHeaderLength;

        // Whole send path (send index, cached encryptor and scratch buffers) under one lock
        // so concurrent Protect calls cannot corrupt the AES-CM state or the SRTCP index.
        lock (_protectSync)
        {
            var index = _sendIndex;

            var result = GC.AllocateUninitializedArray<byte>(
                rtcpPacket.Length + SrtcpIndexLength + _authTagLength);
            rtcpPacket.CopyTo(result);

            // Encrypt from offset 8 to the end of the RTCP packet.
            if (encryptedLen > 0)
            {
                Span<byte> iv = stackalloc byte[AesCmBlockLength];
                BuildIv(ssrc, index, iv);
                AesCmXor(_protectEncryptor, _protectCounterIv, _protectBlock, iv,
                    result.AsSpan(CleartextHeaderLength, encryptedLen));
            }

            // Append the E||index word (E = 1, encryption applied), big-endian.
            var eIndexWord = EncryptionFlag | (index & IndexMask);
            BinaryPrimitives.WriteUInt32BigEndian(
                result.AsSpan(rtcpPacket.Length, SrtcpIndexLength), eIndexWord);

            // Authenticate the cleartext header + ciphertext + E||index word (RFC 3711 §3.4).
            var authenticatedLen = rtcpPacket.Length + SrtcpIndexLength;
            Span<byte> tag = stackalloc byte[AuthTagFullLength];
            ComputeAuthTag(result.AsSpan(0, authenticatedLen), tag);
            tag[.._authTagLength].CopyTo(result.AsSpan(authenticatedLen, _authTagLength));

            // Advance the SRTCP index (31-bit, wraps at 2^31).
            _sendIndex = (index + 1) & IndexMask;

            return result;
        }
    }

    // -------------------------------------------------------------------------
    // Unprotect (authenticate + decrypt inbound SRTCP)
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public byte[] Unprotect(ReadOnlySpan<byte> srtcpPacket)
    {
        var minLength = CleartextHeaderLength + SrtcpIndexLength + _authTagLength;
        if (srtcpPacket.Length < minLength)
            throw new ArgumentException(
                $"SRTCP packet too short (minimum {minLength} bytes).", nameof(srtcpPacket));

        // Layout: [8B cleartext header][encrypted rest][E||index (4B)][auth tag].
        var authenticatedLen = srtcpPacket.Length - _authTagLength;
        var authenticatedSpan = srtcpPacket[..authenticatedLen];
        var receivedTag       = srtcpPacket[authenticatedLen..];

        var eIndexOffset = authenticatedLen - SrtcpIndexLength;
        var eIndexWord   = BinaryPrimitives.ReadUInt32BigEndian(srtcpPacket[eIndexOffset..]);
        var isEncrypted  = (eIndexWord & EncryptionFlag) != 0;
        var index        = eIndexWord & IndexMask;
        var ssrc         = BinaryPrimitives.ReadUInt32BigEndian(srtcpPacket[4..]);

        // Whole receive path (replay window, cached encryptor and scratch buffers) under one
        // lock so concurrent Unprotect calls cannot corrupt the replay window, double-accept
        // a replay, or corrupt the AES-CM state.
        lock (_unprotectSync)
        {
            // 1. Authenticate before decryption (RFC 3711 §3.3 — verify-then-decrypt).
            Span<byte> expectedTag = stackalloc byte[AuthTagFullLength];
            ComputeAuthTag(authenticatedSpan, expectedTag);
            if (!CryptographicOperations.FixedTimeEquals(receivedTag, expectedTag[.._authTagLength]))
                throw new SrtpAuthenticationException("SRTCP authentication tag mismatch.");

            // 2. Replay check over the SRTCP index (RFC 3711 §3.3.2).
            CheckReplay(index);

            // 3. Decrypt the encrypted portion (offset 8 .. E||index) when E = 1.
            var encryptedLen = eIndexOffset - CleartextHeaderLength;
            var output = GC.AllocateUninitializedArray<byte>(eIndexOffset);
            srtcpPacket[..eIndexOffset].CopyTo(output);

            if (isEncrypted && encryptedLen > 0)
            {
                Span<byte> iv = stackalloc byte[AesCmBlockLength];
                BuildIv(ssrc, index, iv);
                AesCmXor(_unprotectEncryptor, _unprotectCounterIv, _unprotectBlock, iv,
                    output.AsSpan(CleartextHeaderLength, encryptedLen));
            }

            // 4. Commit the index to the replay window only after a fully valid packet.
            UpdateReplayWindow(index);

            return output;
        }
    }

    // -------------------------------------------------------------------------
    // AES-CM keystream XOR (RFC 3711 §4.1) — identical construction to SrtpContext.
    // The caller must hold the matching direction lock so the non-thread-safe encryptor
    // and scratch buffers are never touched concurrently.
    // -------------------------------------------------------------------------

    private static void AesCmXor(
        ICryptoTransform encryptor,
        byte[] counterIv,
        byte[] block,
        ReadOnlySpan<byte> iv,
        Span<byte> data)
    {
        if (data.Length > MaxAesCmKeystreamBytes)
            throw new CryptographicException("SRTCP AES-CM payload exceeds the RFC3711 2^16-block keystream limit.");

        iv.CopyTo(counterIv);
        var offset = 0;
        var counter = 0;

        while (offset < data.Length)
        {
            counterIv[14] = (byte)(counter >> 8);
            counterIv[15] = (byte)counter;

            encryptor.TransformBlock(counterIv, 0, AesCmBlockLength, block, 0);

            var chunk = Math.Min(AesCmBlockLength, data.Length - offset);
            for (var i = 0; i < chunk; i++)
                data[offset + i] ^= block[i];

            offset += chunk;
            counter++;
        }
    }

    /// <summary>
    /// Releases the cached AES-CM cipher and encryptor instances held for each direction.
    /// </summary>
    public void Dispose()
    {
        _protectEncryptor.Dispose();
        _unprotectEncryptor.Dispose();
        _protectAes.Dispose();
        _unprotectAes.Dispose();
    }

    // -------------------------------------------------------------------------
    // IV construction (RFC 3711 §4.1.1 for SRTCP)
    // IV = (salt * 2^16) XOR (SSRC * 2^64) XOR (SRTCP index * 2^16), 128-bit big-endian.
    // The 31-bit SRTCP index replaces the SRTP 48-bit packet index and lands in bytes 10..13.
    // -------------------------------------------------------------------------

    private void BuildIv(uint ssrc, uint index, Span<byte> iv)
    {
        iv.Clear();
        _keys.Salt.CopyTo(iv); // k_s * 2^16 leaves bytes 14..15 reserved for the block counter.

        // XOR SSRC into bytes 4..7 (SSRC * 2^64).
        iv[4] ^= (byte)(ssrc >> 24);
        iv[5] ^= (byte)(ssrc >> 16);
        iv[6] ^= (byte)(ssrc >>  8);
        iv[7] ^= (byte) ssrc;

        // XOR the SRTCP index into bytes 10..13 (index * 2^16).
        iv[10] ^= (byte)(index >> 24);
        iv[11] ^= (byte)(index >> 16);
        iv[12] ^= (byte)(index >>  8);
        iv[13] ^= (byte) index;
    }

    // -------------------------------------------------------------------------
    // Authentication (RFC 3711 §4.2) — HMAC-SHA1 over the authenticated portion.
    // SRTCP does not fold in the ROC (that is SRTP-specific); the full index travels in the
    // authenticated E||index word instead.
    // -------------------------------------------------------------------------

    private void ComputeAuthTag(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, _keys.AuthKey);
        hmac.AppendData(data);

        if (!hmac.TryGetHashAndReset(destination, out var bytesWritten)
            || bytesWritten != AuthTagFullLength)
        {
            throw new CryptographicException("Failed to compute SRTCP HMAC-SHA1 authentication tag.");
        }
    }

    // -------------------------------------------------------------------------
    // Replay protection (RFC 3711 §3.3.2) over the 31-bit SRTCP index.
    // -------------------------------------------------------------------------

    private void CheckReplay(uint index)
    {
        if (index > _replayWindowIndex)
            return; // newer than the window — allowed

        var diff = _replayWindowIndex - index;
        if (diff >= ReplayWindowSize)
            throw new SrtpReplayException($"SRTCP index {index} is outside the replay window.");

        if ((_replayWindowBitmap & (1UL << (int)diff)) != 0)
            throw new SrtpReplayException($"SRTCP index {index} has already been received (replay).");
    }

    private void UpdateReplayWindow(uint index)
    {
        if (index > _replayWindowIndex)
        {
            var shift = index - _replayWindowIndex;
            _replayWindowBitmap = shift >= ReplayWindowSize
                ? 0
                : _replayWindowBitmap << (int)shift;
            _replayWindowBitmap |= 1;
            _replayWindowIndex   = index;
        }
        else
        {
            var diff = _replayWindowIndex - index;
            _replayWindowBitmap |= 1UL << (int)diff;
        }
    }
}
