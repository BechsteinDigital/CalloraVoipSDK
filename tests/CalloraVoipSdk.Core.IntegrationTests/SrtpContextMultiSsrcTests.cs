using System.Buffers.Binary;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Per-SSRC SRTP crypto state (ADR-011 B2c-in-1, RFC 3711 §3.2.1): under one shared master key each
/// SSRC advances its own rollover counter and tracks its own replay window, so one context can serve
/// every SSRC a BUNDLE transport (RFC 8843) carries. Inbound state is committed only once a packet
/// from an SSRC authenticates.
/// </summary>
public sealed class SrtpContextMultiSsrcTests
{
    private const int AuthTagLength = 10;
    private const uint SsrcA = 0x0A0A0A0A;
    private const uint SsrcB = 0x0B0B0B0B;

    private static readonly byte[] MasterKey = Convert.FromHexString("E1F97A0D3E018BE0D64FA32C06DE4139");
    private static readonly byte[] MasterSalt = Convert.FromHexString("0EC675AD498AFEEBB6960B3AABE6");

    [Fact]
    public void Each_ssrc_advances_its_rollover_counter_independently()
    {
        // Walk SSRC A across a sequence-number wrap so its ROC advances to 1, then send seq 0 on the
        // fresh SSRC B: B must still be at ROC 0 — byte-identical to a single-stream first packet.
        var sender = new SrtpContext(Material());
        sender.Protect(Packet(SsrcA, seq: ushort.MaxValue, payloadLength: 8));
        sender.Protect(Packet(SsrcA, seq: 0, payloadLength: 8)); // A wraps → A's ROC = 1

        var bFirst = sender.Protect(Packet(SsrcB, seq: 0, payloadLength: 8));

        using var reference = new SrtpContext(Material());
        var expected = reference.Protect(Packet(SsrcB, seq: 0, payloadLength: 8));
        Assert.Equal(expected, bFirst); // B unaffected by A's ROC advancement
    }

    [Fact]
    public void Replay_window_is_tracked_per_ssrc()
    {
        var sender = new SrtpContext(Material());
        var receiver = new SrtpContext(Material());

        var aSeq5 = sender.Protect(Packet(SsrcA, seq: 5, payloadLength: 16));
        var bSeq5 = sender.Protect(Packet(SsrcB, seq: 5, payloadLength: 16));

        receiver.Unprotect(aSeq5);
        // Same sequence number on a different SSRC is a distinct stream — not a replay.
        var bPlain = receiver.Unprotect(bSeq5);
        Assert.Equal(Packet(SsrcB, seq: 5, payloadLength: 16), bPlain);

        // Re-delivering A's packet is a replay on A's own window.
        Assert.Throws<SrtpReplayException>(() => receiver.Unprotect(aSeq5));
    }

    [Fact]
    public void Interleaved_two_ssrc_round_trip_decrypts_both_streams()
    {
        var sender = new SrtpContext(Material());
        var receiver = new SrtpContext(Material());

        for (ushort seq = 0; seq < 4; seq++)
        {
            var a = Packet(SsrcA, seq, payloadLength: 20);
            var b = Packet(SsrcB, seq, payloadLength: 20);
            Assert.Equal(a, receiver.Unprotect(sender.Protect(a)));
            Assert.Equal(b, receiver.Unprotect(sender.Protect(b)));
        }
    }

    [Fact]
    public void A_forged_ssrc_that_fails_authentication_creates_no_state()
    {
        var sender = new SrtpContext(Material());
        var receiver = new SrtpContext(Material());

        // Tamper the auth tag so the packet fails verification.
        var forged = sender.Protect(Packet(SsrcA, seq: 1, payloadLength: 16));
        forged[^1] ^= 0xFF;

        Assert.Throws<SrtpAuthenticationException>(() => receiver.Unprotect(forged));
        Assert.Equal(0, receiver.TrackedSourceCount); // no per-SSRC entry for an unauthenticated source

        // A genuine packet on the same SSRC still authenticates and is treated as its first packet.
        var genuine = sender.Protect(Packet(SsrcA, seq: 1, payloadLength: 16));
        Assert.Equal(Packet(SsrcA, seq: 1, payloadLength: 16), receiver.Unprotect(genuine));
        Assert.Equal(1, receiver.TrackedSourceCount);
    }

    [Fact]
    public void Authenticated_sources_each_get_one_tracked_entry()
    {
        var sender = new SrtpContext(Material());
        var receiver = new SrtpContext(Material());

        receiver.Unprotect(sender.Protect(Packet(SsrcA, seq: 0, payloadLength: 8)));
        receiver.Unprotect(sender.Protect(Packet(SsrcA, seq: 1, payloadLength: 8)));
        receiver.Unprotect(sender.Protect(Packet(SsrcB, seq: 0, payloadLength: 8)));

        Assert.Equal(2, receiver.TrackedSourceCount); // two SSRCs, not four packets
    }

    private static SrtpKeyMaterial Material() =>
        new()
        {
            MasterKey = MasterKey,
            MasterSalt = MasterSalt,
            Suite = SrtpCryptoSuite.AesCm128HmacSha1_80,
        };

    private static byte[] Packet(uint ssrc, ushort seq, int payloadLength)
    {
        var packet = new byte[12 + payloadLength];
        packet[0] = 0x80;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), seq);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), ssrc);
        for (var i = 0; i < payloadLength; i++)
            packet[12 + i] = (byte)(i + seq);
        return packet;
    }
}
