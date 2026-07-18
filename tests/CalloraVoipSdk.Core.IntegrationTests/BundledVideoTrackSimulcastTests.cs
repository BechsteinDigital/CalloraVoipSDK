using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Send-side simulcast (RFC 8853) on the bundled video track: several <c>a=rid</c> encodings ride one MID,
/// each on its own SSRC with the RID SDES header extension (RFC 8852) stamped per packet, so the peer can
/// separate the layers. Proven at the outbound-pipeline layer: each per-rid send goes out on its own SSRC
/// and carries the right MID and RID.
/// </summary>
public sealed class BundledVideoTrackSimulcastTests
{
    private const byte MidExtId = 3;
    private const byte RidExtId = 4;
    private const byte VideoPayloadType = 96;
    private const uint SsrcHi = 0x0B0B0B0B;
    private const uint SsrcLo = 0x0C0C0C0C;
    private const uint RtpTimestamp = 90000;

    private static readonly byte[] MasterKey = Convert.FromHexString("E1F97A0D3E018BE0D64FA32C06DE4139");
    private static readonly byte[] MasterSalt = Convert.FromHexString("0EC675AD498AFEEBB6960B3AABE6");

    [Fact]
    public async Task Each_rid_sends_on_its_own_ssrc_stamped_with_the_mid_and_rid()
    {
        var (outbound, sent) = SimulcastOutbound();
        using var video = SimulcastTrack(outbound, "hi", "lo");

        var frame = SmallH264Frame();
        await video.SendFrameAsync("hi", frame, RtpTimestamp);
        await video.SendFrameAsync("lo", frame, RtpTimestamp);

        var hi = sent.Where(p => p.Ssrc == SsrcHi).ToList();
        var lo = sent.Where(p => p.Ssrc == SsrcLo).ToList();
        Assert.NotEmpty(hi);
        Assert.NotEmpty(lo);
        Assert.Equal(sent.Count, hi.Count + lo.Count); // every packet went out on exactly one layer's SSRC

        foreach (var packet in hi)
        {
            Assert.True(RtpMidHeaderExtension.TryRead(packet.HeaderExtension, MidExtId, out var mid));
            Assert.Equal("video", mid);
            Assert.True(RtpRidHeaderExtension.TryRead(packet.HeaderExtension, RidExtId, out var rid));
            Assert.Equal("hi", rid);
        }

        foreach (var packet in lo)
        {
            Assert.True(RtpRidHeaderExtension.TryRead(packet.HeaderExtension, RidExtId, out var rid));
            Assert.Equal("lo", rid);
        }
    }

    [Fact]
    public void A_simulcast_track_reports_its_rids()
    {
        var (outbound, _) = SimulcastOutbound();
        using var video = SimulcastTrack(outbound, "hi", "lo");

        Assert.True(video.IsSimulcast);
        Assert.Equal(["hi", "lo"], video.SendRids.OrderBy(r => r));
    }

    [Fact]
    public async Task The_no_rid_send_is_rejected_on_a_simulcast_track()
    {
        var (outbound, _) = SimulcastOutbound();
        using var video = SimulcastTrack(outbound, "hi", "lo");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => video.SendFrameAsync(SmallH264Frame(), RtpTimestamp));
    }

    [Fact]
    public async Task Sending_on_an_unknown_rid_throws()
    {
        var (outbound, _) = SimulcastOutbound();
        using var video = SimulcastTrack(outbound, "hi", "lo");

        await Assert.ThrowsAsync<ArgumentException>(
            () => video.SendFrameAsync("mid", SmallH264Frame(), RtpTimestamp));
    }

    [Fact]
    public void A_non_simulcast_track_is_not_flagged_as_simulcast()
    {
        var (outbound, _) = SimulcastOutbound();
        using var video = new BundledVideoTrack(
            "video", "H264", VideoPayloadType, outbound, reorderWindowDepth: 32,
            NullLogger<BundledVideoTrack>.Instance);

        Assert.False(video.IsSimulcast);
        Assert.Empty(video.SendRids);
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    private static BundledVideoTrack SimulcastTrack(BundledOutboundPipeline outbound, params string[] rids) =>
        new("video", "H264", VideoPayloadType, rids, outbound, reorderWindowDepth: 32,
            NullLogger<BundledVideoTrack>.Instance);

    // An outbound pipeline with two per-rid video tracks under MID "video", each stamping its MID+RID.
    private static (BundledOutboundPipeline pipeline, List<RtpPacket> sent) SimulcastOutbound()
    {
        var pipeline = new BundledOutboundPipeline(
            new RtpPacketCodec(), new DiscardSender(), NullLogger<BundledOutboundPipeline>.Instance);
        pipeline.RegisterTrack("video", "hi", EncodingTrack(SsrcHi, "hi"));
        pipeline.RegisterTrack("video", "lo", EncodingTrack(SsrcLo, "lo"));
        pipeline.InstallOutboundKey(new SrtpContext(Material())); // so sends are not fail-closed

        var sent = new List<RtpPacket>();
        pipeline.PacketSent += sent.Add;
        return (pipeline, sent);
    }

    private static BundledOutboundTrack EncodingTrack(uint ssrc, string rid) =>
        new(ssrc, VideoPayloadType, samplesPerPacket: 0,
            new RtpOutboundHeaderExtensionStamper(
                transportWideCcExtensionId: null, MidExtId, "video", RidExtId, rid),
            initialSequenceNumber: 1000, initialTimestamp: RtpTimestamp);

    private static byte[] SmallH264Frame()
    {
        // SPS + PPS + a small IDR in Annex-B form.
        var stream = new MemoryStream();
        foreach (var nal in new[] { Nal(0x67, 12), Nal(0x68, 4), Nal(0x65, 200) })
        {
            stream.Write(new byte[] { 0, 0, 1 });
            stream.Write(nal);
        }
        return stream.ToArray();
    }

    private static byte[] Nal(byte header, int bodyLength)
    {
        var nal = new byte[1 + bodyLength];
        nal[0] = header;
        for (var i = 1; i < nal.Length; i++)
            nal[i] = (byte)(1 + (i % 250)); // never 0x00 — keeps Annex-B parsing unambiguous
        return nal;
    }

    private static SrtpKeyMaterial Material() =>
        new() { MasterKey = MasterKey, MasterSalt = MasterSalt, Suite = SrtpCryptoSuite.AesCm128HmacSha1_80 };

    private sealed class DiscardSender : IBundledDatagramSender
    {
        public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
