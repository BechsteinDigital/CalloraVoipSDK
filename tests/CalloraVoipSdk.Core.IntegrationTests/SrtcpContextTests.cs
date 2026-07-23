using System.Buffers.Binary;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// SRTCP context tests (RFC 3711 §3.4). The key-derivation known-answers for labels 3/4/5
/// were computed independently with an AES-CM PRF that reproduces the RFC 3711 §B.3 SRTP
/// vectors exactly (labels 0/1/2), so they verify the SRTCP KDF, not just round-trip.
/// </summary>
public sealed class SrtcpContextTests
{
    // RFC 3711 §B.3 master key/salt (same as SrtpContextTests).
    private const string RfcMasterKeyHex = "E1F97A0D3E018BE0D64FA32C06DE4139";
    private const string RfcMasterSaltHex = "0EC675AD498AFEEBB6960B3AABE6";

    // Independently derived SRTCP session keys (labels 3/4/5) for the RFC master material.
    private static readonly byte[] RtcpCipherKey = Convert.FromHexString("4C1AA45A81F73D61C800BBB00FBB1EAA");
    private static readonly byte[] RtcpAuthKey = Convert.FromHexString("8D54534FEB49AE8E7993A6BD0B844FC323A93DFD");
    private static readonly byte[] RtcpSalt = Convert.FromHexString("9581C7AD87B3E530BF3E4454A8B3");

    private const int AuthTagLength = 10;
    private const int IndexLength = 4;

    private static SrtpKeyMaterial RfcMaterial() =>
        SrtpKeyMaterial.ParseInline(
            "inline:" + Convert.ToBase64String(Convert.FromHexString(RfcMasterKeyHex + RfcMasterSaltHex)),
            SrtpCryptoSuite.AesCm128HmacSha1_80);

    private static SrtpKeyMaterial Material(byte seed) =>
        Material(seed, SrtpCryptoSuite.AesCm128HmacSha1_80);

    private static SrtpKeyMaterial Material(byte seed, SrtpCryptoSuite suite)
    {
        var material = new byte[30];
        for (var i = 0; i < material.Length; i++)
            material[i] = (byte)(seed + i);
        return SrtpKeyMaterial.ParseInline(
            "inline:" + Convert.ToBase64String(material), suite);
    }

    private static byte[] Rtcp(uint ssrc, int payloadLength)
    {
        var packet = new byte[8 + payloadLength];
        packet[0] = 0x81;              // V=2, RC=1
        packet[1] = 200;               // PT = SR
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)((packet.Length / 4) - 1));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), ssrc);
        for (var i = 8; i < packet.Length; i++)
            packet[i] = (byte)(0xA0 + i);
        return packet;
    }

    // ── KDF: independent known-answer for SRTCP labels 3/4/5 ──────────────────────

    [Fact]
    public void DeriveRtcp_matches_independent_kdf_known_answer()
    {
        var keys = SrtpKeyDerivation.DeriveRtcp(RfcMaterial());

        Assert.Equal(RtcpCipherKey, keys.CipherKey);
        Assert.Equal(RtcpAuthKey, keys.AuthKey);
        Assert.Equal(RtcpSalt, keys.Salt);
    }

    [Fact]
    public void Srtcp_keys_differ_from_srtp_keys()
    {
        var material = RfcMaterial();
        var srtp = SrtpKeyDerivation.Derive(material);
        var srtcp = SrtpKeyDerivation.DeriveRtcp(material);

        Assert.NotEqual(srtp.CipherKey, srtcp.CipherKey);
        Assert.NotEqual(srtp.AuthKey, srtcp.AuthKey);
        Assert.NotEqual(srtp.Salt, srtcp.Salt);
    }

    // ── Packet shape: clear header, encrypted payload, index + tag appended ───────

    [Fact]
    public void Protect_keeps_header_clear_encrypts_payload_and_appends_index_and_tag()
    {
        var packet = Rtcp(ssrc: 0x0A0B0C0D, payloadLength: 40);
        using var ctx = new SrtcpContext(Material(1));

        var protectedPacket = ctx.ProtectRtcp(packet);

        Assert.Equal(packet.Length + IndexLength + AuthTagLength, protectedPacket.Length);
        // First 8 bytes stay in the clear.
        Assert.Equal(packet[..8], protectedPacket[..8]);
        // Payload (bytes 8..) is encrypted.
        Assert.NotEqual(packet[8..], protectedPacket[8..packet.Length]);
        // E-flag set, index = 1 for the first packet.
        var indexWord = BinaryPrimitives.ReadUInt32BigEndian(protectedPacket.AsSpan(packet.Length));
        Assert.Equal(0x8000_0001u, indexWord);
    }

    [Fact]
    public void Sender_index_increments_per_packet()
    {
        var packet = Rtcp(ssrc: 1, payloadLength: 16);
        using var ctx = new SrtcpContext(Material(2));

        var first = ctx.ProtectRtcp(packet);
        var second = ctx.ProtectRtcp(packet);

        Assert.Equal(0x8000_0001u, BinaryPrimitives.ReadUInt32BigEndian(first.AsSpan(packet.Length)));
        Assert.Equal(0x8000_0002u, BinaryPrimitives.ReadUInt32BigEndian(second.AsSpan(packet.Length)));
    }

    // ── RFC 4568 §6.2 / RFC 5764 §4.1.2: SRTCP keeps the 80-bit tag even for SHA1_32 ──

    [Theory]
    [InlineData(false)] // AES_CM_128_HMAC_SHA1_80
    [InlineData(true)]  // AES_CM_128_HMAC_SHA1_32 — SRTP tag would be 32 bit, SRTCP stays 80 bit
    public void Srtcp_tag_is_80_bit_for_every_suite(bool sha1_32)
    {
        var suite = sha1_32
            ? SrtpCryptoSuite.AesCm128HmacSha1_32
            : SrtpCryptoSuite.AesCm128HmacSha1_80;
        var packet = Rtcp(ssrc: 0x11223344, payloadLength: 24);
        using var ctx = new SrtcpContext(Material(11, suite));

        var protectedPacket = ctx.ProtectRtcp(packet);

        // The 32-bit truncation of RFC 3711 §5.2 is SRTP-only; SRTCP always appends a 10-byte tag.
        Assert.Equal(packet.Length + IndexLength + AuthTagLength, protectedPacket.Length);
    }

    [Fact]
    public void Sha1_32_suite_roundtrips_with_80_bit_srtcp_tag()
    {
        var packet = Rtcp(ssrc: 0x55667788, payloadLength: 28);
        using var sender = new SrtcpContext(Material(12, SrtpCryptoSuite.AesCm128HmacSha1_32));
        using var receiver = new SrtcpContext(Material(12, SrtpCryptoSuite.AesCm128HmacSha1_32));

        var recovered = receiver.UnprotectRtcp(sender.ProtectRtcp(packet));

        Assert.Equal(packet, recovered);
    }

    // ── Round-trip through a peer context (same master key, both directions) ──────

    [Fact]
    public void Roundtrip_via_peer_context_returns_original()
    {
        var packet = Rtcp(ssrc: 0xCAFEBABE, payloadLength: 44);
        using var sender = new SrtcpContext(Material(3));
        using var receiver = new SrtcpContext(Material(3));

        var protectedPacket = sender.ProtectRtcp(packet);
        var recovered = receiver.UnprotectRtcp(protectedPacket);

        Assert.Equal(packet, recovered);
    }

    [Fact]
    public void Roundtrip_with_empty_payload_returns_original()
    {
        var packet = Rtcp(ssrc: 7, payloadLength: 0);
        using var sender = new SrtcpContext(Material(4));
        using var receiver = new SrtcpContext(Material(4));

        var recovered = receiver.UnprotectRtcp(sender.ProtectRtcp(packet));

        Assert.Equal(packet, recovered);
    }

    // ── Security: tamper + replay ─────────────────────────────────────────────────

    [Fact]
    public void Tampered_tag_throws_authentication()
    {
        var packet = Rtcp(ssrc: 5, payloadLength: 20);
        using var sender = new SrtcpContext(Material(5));
        using var receiver = new SrtcpContext(Material(5));

        var protectedPacket = sender.ProtectRtcp(packet);
        protectedPacket[^1] ^= 0xFF; // flip a tag byte

        Assert.Throws<SrtpAuthenticationException>(() => receiver.UnprotectRtcp(protectedPacket));
    }

    [Fact]
    public void Wrong_key_fails_authentication()
    {
        var packet = Rtcp(ssrc: 5, payloadLength: 20);
        using var sender = new SrtcpContext(Material(6));
        using var receiver = new SrtcpContext(Material(7)); // different master key

        Assert.Throws<SrtpAuthenticationException>(() => receiver.UnprotectRtcp(sender.ProtectRtcp(packet)));
    }

    [Fact]
    public void Replayed_packet_is_rejected()
    {
        var packet = Rtcp(ssrc: 9, payloadLength: 12);
        using var sender = new SrtcpContext(Material(8));
        using var receiver = new SrtcpContext(Material(8));

        var protectedPacket = sender.ProtectRtcp(packet);
        _ = receiver.UnprotectRtcp(protectedPacket);

        Assert.Throws<SrtpReplayException>(() => receiver.UnprotectRtcp(protectedPacket));
    }

    [Fact]
    public void Disposed_context_rejects_use_and_zeroes_keys()
    {
        var ctx = new SrtcpContext(Material(9));
        var keys = ctx.SessionKeys;
        ctx.Dispose();

        Assert.All(keys.CipherKey, b => Assert.Equal(0, b));
        Assert.Throws<ObjectDisposedException>(() => ctx.ProtectRtcp(Rtcp(1, 8)));
    }
}
