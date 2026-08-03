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
    private const uint SsrcC = 0x0C0C0C0C;

    private static readonly byte[] MasterKey = Convert.FromHexString("E1F97A0D3E018BE0D64FA32C06DE4139");
    private static readonly byte[] MasterSalt = Convert.FromHexString("0EC675AD498AFEEBB6960B3AABE6");
    // AEAD-GCM (RFC 7714) takes a 96-bit (12-byte) master salt, unlike AES-CM's 112-bit salt.
    private static readonly byte[] GcmSalt = Convert.FromHexString("0EC675AD498AFEEBB6960B3A");

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

    // -------------------------------------------------------------------------
    // Per-SSRC state cap (#157 P1-2, RFC 3711 §3.2.1 state, K4 wire-DoS): a peer holding the
    // session key can spray arbitrarily many authenticated SSRCs; auth-before-admission stops a
    // forged flood but not a keyed one, so the per-SSRC state map must be hard-bounded. At the cap a
    // NEW source is discarded (never evicts an active replay window — that would let old replays back
    // in), while every already-admitted SSRC keeps flowing.
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(false)] // AES-CM + HMAC-SHA1-80
    [InlineData(true)]  // AEAD-AES-128-GCM
    public void A_new_ssrc_beyond_the_tracked_source_cap_is_rejected_without_growing_or_evicting(
        bool aead)
    {
        var sender = new SrtpContext(Material(aead));
        using var receiver = new SrtpContext(Material(aead), maxTrackedSsrcs: 2);

        // Fill the cap with two authenticated SSRCs.
        receiver.Unprotect(sender.Protect(Packet(SsrcA, seq: 0, payloadLength: 16)));
        receiver.Unprotect(sender.Protect(Packet(SsrcB, seq: 0, payloadLength: 16)));
        Assert.Equal(2, receiver.TrackedSourceCount);

        // A third authenticated SSRC at the full map is a controlled discard, not an eviction.
        var third = sender.Protect(Packet(SsrcC, seq: 0, payloadLength: 16));
        Assert.Throws<SrtpSourceLimitException>(() => receiver.Unprotect(third));
        Assert.Equal(2, receiver.TrackedSourceCount);   // map did not grow
        Assert.Equal(1L, receiver.DiscardedSourceCount); // cap hit counted for telemetry

        // The already-admitted SSRCs keep flowing — A's replay window was not evicted.
        var aNext = sender.Protect(Packet(SsrcA, seq: 1, payloadLength: 16));
        Assert.Equal(Packet(SsrcA, seq: 1, payloadLength: 16), receiver.Unprotect(aNext));
    }

    [Theory]
    [InlineData(false)] // AES-CM + HMAC-SHA1-80
    [InlineData(true)]  // AEAD-AES-128-GCM
    public void An_authenticated_ssrc_flood_holds_the_tracked_state_at_the_cap(bool aead)
    {
        const int cap = 4;
        var sender = new SrtpContext(Material(aead));
        using var receiver = new SrtpContext(Material(aead), maxTrackedSsrcs: cap);

        var admitted = 0;
        for (uint i = 0; i < 16; i++)
        {
            var packet = sender.Protect(Packet(0x1000_0000 + i, seq: 0, payloadLength: 12));
            try { receiver.Unprotect(packet); admitted++; }
            catch (SrtpSourceLimitException) { /* over-cap: controlled discard */ }
        }

        Assert.Equal(cap, admitted);
        Assert.Equal(cap, receiver.TrackedSourceCount);       // constant upper bound on state (K4)
        Assert.Equal(16L - cap, receiver.DiscardedSourceCount);
    }

    [Fact]
    public void The_outbound_protect_path_is_not_capped_because_a_sender_controls_its_own_ssrcs()
    {
        // The cap is a receive-side defense against a keyed peer (#157 P1-2); a sender's own SSRCs are
        // self-bounded, so Protect must never throw SrtpSourceLimitException and break a multi-stream send.
        using var sender = new SrtpContext(Material(aead: false), maxTrackedSsrcs: 2);
        for (uint i = 0; i < 8; i++)
            _ = sender.Protect(Packet(0x3000_0000 + i, seq: 0, payloadLength: 8));
        Assert.Equal(8, sender.TrackedSourceCount); // all outbound SSRCs tracked; the cap did not apply
    }

    private static SrtpKeyMaterial Material() =>
        new(MasterKey, MasterSalt, SrtpCryptoSuite.AesCm128HmacSha1_80);

    private static SrtpKeyMaterial Material(bool aead) => aead
        ? new(MasterKey, GcmSalt, SrtpCryptoSuite.AeadAes128Gcm)
        : new(MasterKey, MasterSalt, SrtpCryptoSuite.AesCm128HmacSha1_80);

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
