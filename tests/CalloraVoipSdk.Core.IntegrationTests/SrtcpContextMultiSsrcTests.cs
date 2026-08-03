using System.Buffers.Binary;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Per-SSRC SRTCP index and replay state (HARD-D1, RFC 3711 §3.2.3). Over a BUNDLE (RFC 8843) several
/// RTCP sources are multiplexed under one shared SRTCP key; each advances its own SRTCP index from 1.
/// A single receive context must key its replay window per SSRC, so a second source's index 1 is not
/// rejected as a replay of the first's — while a genuine per-SSRC replay is still refused.
/// </summary>
public sealed class SrtcpContextMultiSsrcTests
{
    private const uint AudioSsrc = 0x0A0A0A0A;
    private const uint VideoSsrc = 0x0B0B0B0B;
    private const uint DataSsrc = 0x0C0C0C0C;

    private static readonly byte[] MasterKey = Convert.FromHexString("E1F97A0D3E018BE0D64FA32C06DE4139");
    private static readonly byte[] MasterSalt = Convert.FromHexString("0EC675AD498AFEEBB6960B3AABE6");
    // AEAD-GCM (RFC 7714) takes a 96-bit (12-byte) master salt, unlike AES-CM's 112-bit salt.
    private static readonly byte[] GcmSalt = Convert.FromHexString("0EC675AD498AFEEBB6960B3A");

    [Fact]
    public void Bundled_ssrcs_do_not_collide_in_the_srtcp_replay_window()
    {
        // Two independent senders (as over a BUNDLE) both start at SRTCP index 1 for their own SSRC.
        using var audioSender = new SrtcpContext(Material());
        using var videoSender = new SrtcpContext(Material());
        using var receiver = new SrtcpContext(Material());

        var audio1 = audioSender.ProtectRtcp(Rtcp(AudioSsrc));
        var video1 = videoSender.ProtectRtcp(Rtcp(VideoSsrc)); // same index 1, different SSRC

        // Both accepted: a shared single window would reject video1 as a replay of audio1's index 1.
        _ = receiver.UnprotectRtcp(audio1);
        _ = receiver.UnprotectRtcp(video1);

        // The per-SSRC window still advances and still refuses a real replay.
        var audio2 = audioSender.ProtectRtcp(Rtcp(AudioSsrc));
        _ = receiver.UnprotectRtcp(audio2);
        Assert.Throws<SrtpReplayException>(() => receiver.UnprotectRtcp(audio1));
    }

    // -------------------------------------------------------------------------
    // Per-SSRC SRTCP state cap (#157 P1-2, RFC 3711 §3.2.3 state, K4 wire-DoS): a keyed peer can
    // spray arbitrarily many authenticated RTCP SSRCs. The per-SSRC index/replay map is hard-bounded;
    // at the cap a new source is discarded (never evicting an active window) while known sources flow.
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(false)] // AES-CM + HMAC-SHA1-80
    [InlineData(true)]  // AEAD-AES-128-GCM
    public void A_new_srtcp_source_beyond_the_cap_is_rejected_without_growing_or_evicting(bool aead)
    {
        using var sender = new SrtcpContext(Material(aead));
        using var receiver = new SrtcpContext(Material(aead), maxTrackedSsrcs: 2);

        _ = receiver.UnprotectRtcp(sender.ProtectRtcp(Rtcp(AudioSsrc)));
        _ = receiver.UnprotectRtcp(sender.ProtectRtcp(Rtcp(VideoSsrc)));
        Assert.Equal(2, receiver.TrackedSourceCount);

        var third = sender.ProtectRtcp(Rtcp(DataSsrc));
        Assert.Throws<SrtpSourceLimitException>(() => receiver.UnprotectRtcp(third));
        Assert.Equal(2, receiver.TrackedSourceCount);    // map did not grow
        Assert.Equal(1L, receiver.DiscardedSourceCount); // cap hit counted for telemetry

        // A known source keeps flowing — its replay window was not evicted.
        _ = receiver.UnprotectRtcp(sender.ProtectRtcp(Rtcp(AudioSsrc)));
    }

    [Theory]
    [InlineData(false)] // AES-CM + HMAC-SHA1-80
    [InlineData(true)]  // AEAD-AES-128-GCM
    public void An_srtcp_source_flood_holds_the_tracked_state_at_the_cap(bool aead)
    {
        const int cap = 4;
        using var sender = new SrtcpContext(Material(aead));
        using var receiver = new SrtcpContext(Material(aead), maxTrackedSsrcs: cap);

        var admitted = 0;
        for (uint i = 0; i < 16; i++)
        {
            var packet = sender.ProtectRtcp(Rtcp(0x2000_0000 + i));
            try { _ = receiver.UnprotectRtcp(packet); admitted++; }
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
        // self-bounded, so ProtectRtcp must never throw SrtpSourceLimitException and break a multi-stream send.
        using var sender = new SrtcpContext(Material(aead: false), maxTrackedSsrcs: 2);
        for (uint i = 0; i < 8; i++)
            _ = sender.ProtectRtcp(Rtcp(0x3000_0000 + i));
        Assert.Equal(8, sender.TrackedSourceCount); // all outbound SSRCs tracked; the cap did not apply
    }

    // -------------------------------------------------------------------------
    // Send-index / key-use lifetime (#157 P1-1, RFC 3711 §9.2): the 31-bit SRTCP index must never wrap
    // under one key — a wrap reuses the AES-CM keystream / GCM nonce. Before exhaustion the sender fails
    // closed with a typed exception. A tiny injected limit stands in for 2^31 in the test.
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(false)] // AES-CM + HMAC-SHA1-80
    [InlineData(true)]  // AEAD-AES-128-GCM
    public void The_last_allowed_srtcp_send_index_works_and_the_next_fails_closed_without_wrapping(bool aead)
    {
        const uint maxIndex = 4;
        using var sender = new SrtcpContext(Material(aead), maxSendIndex: maxIndex);

        // NextSendIndex pre-increments: indices 1..4 are within the key's lifetime and protect a packet.
        for (var i = 0; i < maxIndex; i++)
            _ = sender.ProtectRtcp(Rtcp(AudioSsrc));

        // The next index (5) would exceed the limit → fail closed: no packet, no wrap, no reuse.
        Assert.Throws<SrtpKeyLifetimeExceededException>(() => sender.ProtectRtcp(Rtcp(AudioSsrc)));
    }

    private static SrtpKeyMaterial Material() =>
        new(MasterKey, MasterSalt, SrtpCryptoSuite.AesCm128HmacSha1_80);

    private static SrtpKeyMaterial Material(bool aead) => aead
        ? new(MasterKey, GcmSalt, SrtpCryptoSuite.AeadAes128Gcm)
        : new(MasterKey, MasterSalt, SrtpCryptoSuite.AesCm128HmacSha1_80);

    // Minimal 8-byte RTCP receiver report: header (V=2, PT=201) + sender SSRC, no payload.
    private static byte[] Rtcp(uint ssrc)
    {
        var packet = new byte[8];
        packet[0] = 0x80;
        packet[1] = 201;
        packet[3] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), ssrc);
        return packet;
    }
}
