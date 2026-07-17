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

    private static SrtpKeyMaterial Material() => new()
    {
        MasterKey = Convert.FromHexString("E1F97A0D3E018BE0D64FA32C06DE4139"),
        MasterSalt = Convert.FromHexString("0EC675AD498AFEEBB6960B3AABE6"),
        Suite = SrtpCryptoSuite.AesCm128HmacSha1_80,
    };

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
