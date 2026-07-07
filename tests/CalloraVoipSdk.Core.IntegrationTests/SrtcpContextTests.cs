using System.Buffers.Binary;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Correctness of <see cref="SrtcpContext"/> (RFC 3711 §3.4): AES-CM encryption from offset 8,
/// mandatory HMAC-SHA1 authentication over the E||index-suffixed packet, and replay protection
/// over the 31-bit SRTCP index. Verified by internal round-trip, tamper and replay tests — not
/// by live interop against a third-party peer.
/// </summary>
public sealed class SrtcpContextTests
{
    private const int CleartextHeaderLength = 8;
    private const int SrtcpIndexLength = 4;
    private const int AuthTagLength = 10;
    private const uint SenderSsrc = 0x1BADD00Du;

    private static readonly byte[] MasterKey = Convert.FromHexString("E1F97A0D3E018BE0D64FA32C06DE4139");
    private static readonly byte[] MasterSalt = Convert.FromHexString("0EC675AD498AFEEBB6960B3AABE6");

    [Fact]
    public void ProtectThenUnprotect_RoundTripsRealisticCompound_AcrossConsecutiveIndices()
    {
        using var context = new SrtcpContext(CreateMaterial());

        for (var i = 0; i < 8; i++)
        {
            var rtcp = CreateSenderReportCompound(SenderSsrc, seed: (byte)(i * 11 + 1));

            var protectedPacket = context.Protect(rtcp);

            // Structural expectations: cleartext 8-byte header, encrypted body, E=1, tag appended.
            Assert.Equal(rtcp.Length + SrtcpIndexLength + AuthTagLength, protectedPacket.Length);
            Assert.Equal(rtcp.AsSpan(0, CleartextHeaderLength).ToArray(),
                protectedPacket.AsSpan(0, CleartextHeaderLength).ToArray());
            Assert.False(
                protectedPacket.AsSpan(CleartextHeaderLength, rtcp.Length - CleartextHeaderLength)
                    .SequenceEqual(rtcp.AsSpan(CleartextHeaderLength)),
                "Body past the 8-byte header must be encrypted, not cleartext.");
            Assert.Equal(i, (int)(ReadEIndexWord(protectedPacket) & 0x7FFF_FFFFu));
            Assert.True((ReadEIndexWord(protectedPacket) & 0x8000_0000u) != 0, "E flag must be set (E=1).");

            var recovered = context.Unprotect(protectedPacket);
            Assert.Equal(rtcp, recovered);
        }
    }

    [Fact]
    public void Unprotect_TamperedCiphertextByte_ThrowsAuthentication()
    {
        using var context = new SrtcpContext(CreateMaterial());
        var protectedPacket = context.Protect(CreateSenderReportCompound(SenderSsrc, seed: 42));

        // Flip a byte inside the encrypted body (offset 12 is within the sender info).
        protectedPacket[12] ^= 0xFF;

        Assert.Throws<SrtpAuthenticationException>(() => context.Unprotect(protectedPacket));
    }

    [Fact]
    public void Unprotect_TamperedEIndexWord_ThrowsAuthentication()
    {
        using var context = new SrtcpContext(CreateMaterial());
        var protectedPacket = context.Protect(CreateSenderReportCompound(SenderSsrc, seed: 7));

        // Flip a bit in the low byte of the E||index word (authenticated portion).
        var eIndexOffset = protectedPacket.Length - AuthTagLength - SrtcpIndexLength;
        protectedPacket[eIndexOffset + 3] ^= 0x01;

        Assert.Throws<SrtpAuthenticationException>(() => context.Unprotect(protectedPacket));
    }

    [Fact]
    public void Unprotect_SameIndexTwice_ThrowsReplayOnSecond()
    {
        using var context = new SrtcpContext(CreateMaterial());
        var protectedPacket = context.Protect(CreateSenderReportCompound(SenderSsrc, seed: 99));

        var first = context.Unprotect(protectedPacket);
        Assert.NotNull(first);

        Assert.Throws<SrtpReplayException>(() => context.Unprotect(protectedPacket));
    }

    [Fact]
    public void Protect_OnOneContext_UnprotectsOnPeerContextWithSameKey()
    {
        // Direction A protects with its (Local) key material; direction B holds the identical
        // key material as its inbound context and must read A's packets back to cleartext.
        using var producer = new SrtcpContext(CreateMaterial());
        using var consumer = new SrtcpContext(CreateMaterial());

        for (var i = 0; i < 4; i++)
        {
            var rtcp = CreateSenderReportCompound(SenderSsrc, seed: (byte)(i * 17 + 3));
            var protectedPacket = producer.Protect(rtcp);
            Assert.Equal(rtcp, consumer.Unprotect(protectedPacket));
        }
    }

    [Fact]
    public void Unprotect_PacketTooShort_ThrowsArgumentException()
    {
        using var context = new SrtcpContext(CreateMaterial());
        // Below the minimum (8 header + 4 index + 10 tag = 22 bytes).
        Assert.Throws<ArgumentException>(() => context.Unprotect(new byte[10]));
    }

    private static SrtpKeyMaterial CreateMaterial() =>
        new()
        {
            MasterKey = MasterKey,
            MasterSalt = MasterSalt,
            Suite = SrtpCryptoSuite.AesCm128HmacSha1_80,
        };

    private static uint ReadEIndexWord(byte[] protectedPacket)
    {
        var eIndexOffset = protectedPacket.Length - AuthTagLength - SrtcpIndexLength;
        return BinaryPrimitives.ReadUInt32BigEndian(protectedPacket.AsSpan(eIndexOffset));
    }

    /// <summary>
    /// Builds a realistic RTCP compound: a Sender Report (PT=200, 28 bytes) followed by a
    /// minimal empty SDES (PT=202) to exercise a multi-packet compound past one AES block.
    /// </summary>
    private static byte[] CreateSenderReportCompound(uint senderSsrc, byte seed)
    {
        // Sender Report: 8-byte header + 20-byte sender info = 28 bytes, RC = 0.
        var sr = new byte[28];
        sr[0] = 0x80;            // V=2, P=0, RC=0
        sr[1] = 200;             // PT = SR
        BinaryPrimitives.WriteUInt16BigEndian(sr.AsSpan(2), 6); // length in words - 1
        BinaryPrimitives.WriteUInt32BigEndian(sr.AsSpan(4), senderSsrc);
        for (var i = 8; i < sr.Length; i++)
            sr[i] = (byte)(seed + i);

        // Minimal SDES with one empty chunk: 8 bytes, SC = 1.
        var sdes = new byte[8];
        sdes[0] = 0x81;          // V=2, P=0, SC=1
        sdes[1] = 202;           // PT = SDES
        BinaryPrimitives.WriteUInt16BigEndian(sdes.AsSpan(2), 1); // length in words - 1
        BinaryPrimitives.WriteUInt32BigEndian(sdes.AsSpan(4), senderSsrc);

        var compound = new byte[sr.Length + sdes.Length];
        sr.CopyTo(compound.AsSpan());
        sdes.CopyTo(compound.AsSpan(sr.Length));
        return compound;
    }
}
