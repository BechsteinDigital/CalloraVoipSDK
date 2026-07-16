using System.Buffers.Binary;
using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The bundled transport's inbound pipeline (ADR-011 B2c-in-3): one shared 5-tuple carrying STUN,
/// DTLS, RTCP, and multiple RTP streams is classified (RFC 7983), decrypted with the shared SRTP/SRTCP
/// contexts, and each RTP stream is routed to the owning m-line's track sink. BUNDLE media is
/// DTLS-SRTP only, so RTP/RTCP received before keying fails closed.
/// </summary>
public sealed class BundledInboundPipelineTests
{
    private const byte AudioPayloadType = 0;
    private const byte VideoPayloadType = 96;
    private const uint AudioSsrc = 0x0A0A0A0A;
    private const uint VideoSsrc = 0x0B0B0B0B;

    private static readonly byte[] MasterKey = Convert.FromHexString("E1F97A0D3E018BE0D64FA32C06DE4139");
    private static readonly byte[] MasterSalt = Convert.FromHexString("0EC675AD498AFEEBB6960B3AABE6");
    private static readonly IPEndPoint Peer = new(IPAddress.Loopback, 40000);

    [Fact]
    public void Rtp_streams_route_to_their_track_sinks_decrypted()
    {
        var harness = new Harness();
        var sender = new SrtpContext(Material());

        harness.Pipeline.ProcessInboundDatagram(
            sender.Protect(Rtp(AudioSsrc, AudioPayloadType, seq: 1, payload: [1, 2, 3])), Peer);
        harness.Pipeline.ProcessInboundDatagram(
            sender.Protect(Rtp(VideoSsrc, VideoPayloadType, seq: 1, payload: [9, 8, 7, 6])), Peer);

        var audio = Assert.Single(harness.Audio);
        Assert.Equal(AudioSsrc, audio.Ssrc);
        Assert.Equal(new byte[] { 1, 2, 3 }, audio.Payload.ToArray());

        var video = Assert.Single(harness.Video);
        Assert.Equal(VideoSsrc, video.Ssrc);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, video.Payload.ToArray());

        Assert.Equal(0, harness.Pipeline.DroppedDatagrams);
        Assert.Equal(0, harness.Router.DroppedPackets);
    }

    [Fact]
    public void Rtp_before_keying_fails_closed()
    {
        var harness = new Harness(installKeys: false);
        var sender = new SrtpContext(Material());

        harness.Pipeline.ProcessInboundDatagram(
            sender.Protect(Rtp(AudioSsrc, AudioPayloadType, seq: 1, payload: [1, 2, 3])), Peer);

        Assert.Empty(harness.Audio);
        Assert.Equal(1, harness.Pipeline.DroppedDatagrams);
    }

    [Fact]
    public void Rtp_failing_authentication_is_dropped_not_thrown()
    {
        var harness = new Harness();
        var sender = new SrtpContext(Material());
        var forged = sender.Protect(Rtp(AudioSsrc, AudioPayloadType, seq: 1, payload: [1, 2, 3]));
        forged[^1] ^= 0xFF; // corrupt the auth tag

        harness.Pipeline.ProcessInboundDatagram(forged, Peer);

        Assert.Empty(harness.Audio);
        Assert.Equal(1, harness.Pipeline.DroppedDatagrams);
    }

    [Fact]
    public void An_rtp_packet_for_an_unregistered_track_is_counted_by_the_router()
    {
        var harness = new Harness();
        var sender = new SrtpContext(Material());
        harness.Router.UnregisterTrack("video");

        harness.Pipeline.ProcessInboundDatagram(
            sender.Protect(Rtp(VideoSsrc, VideoPayloadType, seq: 1, payload: [4, 5])), Peer);

        Assert.Empty(harness.Video);
        Assert.Equal(0, harness.Pipeline.DroppedDatagrams); // decrypted fine — the drop is a routing drop
        Assert.Equal(1, harness.Router.DroppedPackets);
    }

    [Fact]
    public void A_stun_datagram_is_handed_to_the_ice_layer_not_the_tracks()
    {
        var harness = new Harness();
        byte[]? received = null;
        IPEndPoint? from = null;
        harness.Pipeline.StunPacketReceived += (d, s) => { received = d; from = s; };

        var stun = new byte[20];
        stun[0] = 0x00;
        BinaryPrimitives.WriteUInt32BigEndian(stun.AsSpan(4), 0x2112A442u);
        harness.Pipeline.ProcessInboundDatagram(stun, Peer);

        Assert.Equal(stun, received);
        Assert.Same(Peer, from);
        Assert.Empty(harness.Audio);
        Assert.Equal(0, harness.Pipeline.DroppedDatagrams);
    }

    [Fact]
    public void A_dtls_record_is_handed_to_the_handshake_layer()
    {
        var harness = new Harness();
        byte[]? received = null;
        harness.Pipeline.DtlsPacketReceived += (d, _) => received = d;

        var dtls = new byte[13];
        dtls[0] = 22; // handshake content type
        harness.Pipeline.ProcessInboundDatagram(dtls, Peer);

        Assert.Equal(dtls, received);
        Assert.Empty(harness.Audio);
    }

    [Fact]
    public void Rtcp_is_decrypted_through_the_shared_context_and_dispatched()
    {
        var harness = new Harness();
        var senderSrtcp = new SrtcpContext(Material());
        byte[]? control = null;
        harness.Pipeline.ControlPacketReceived += d => control = d;

        var rtcp = Rtcp(AudioSsrc, payloadLength: 16);
        harness.Pipeline.ProcessInboundDatagram(senderSrtcp.ProtectRtcp(rtcp), Peer);

        Assert.Equal(rtcp, control);
        Assert.Equal(0, harness.Pipeline.DroppedDatagrams);
    }

    [Fact]
    public void Rtcp_before_keying_fails_closed()
    {
        var harness = new Harness(installKeys: false);
        var senderSrtcp = new SrtcpContext(Material());
        var raised = false;
        harness.Pipeline.ControlPacketReceived += _ => raised = true;

        harness.Pipeline.ProcessInboundDatagram(senderSrtcp.ProtectRtcp(Rtcp(AudioSsrc, 16)), Peer);

        Assert.False(raised);
        Assert.Equal(1, harness.Pipeline.DroppedDatagrams);
    }

    private sealed class Harness
    {
        public BundledInboundPipeline Pipeline { get; }
        public BundledTrackRouter Router { get; }
        public List<RtpPacket> Audio { get; } = [];
        public List<RtpPacket> Video { get; } = [];

        public Harness(bool installKeys = true)
        {
            var demux = BundledRtpDemultiplexerFactory.Create(
                midExtensionId: 0,
                new Dictionary<string, IReadOnlyCollection<int>>
                {
                    ["audio"] = new[] { (int)AudioPayloadType },
                    ["video"] = new[] { (int)VideoPayloadType },
                });
            Router = new BundledTrackRouter(demux);
            Router.RegisterTrack("audio", Audio.Add);
            Router.RegisterTrack("video", Video.Add);

            Pipeline = new BundledInboundPipeline(
                Router, new RtpPacketCodec(), NullLogger<BundledInboundPipeline>.Instance);

            if (installKeys)
                Pipeline.InstallInboundKeys(new SrtpContext(Material()), new SrtcpContext(Material()));
        }
    }

    private static SrtpKeyMaterial Material() =>
        new()
        {
            MasterKey = MasterKey,
            MasterSalt = MasterSalt,
            Suite = SrtpCryptoSuite.AesCm128HmacSha1_80,
        };

    private static byte[] Rtp(uint ssrc, byte payloadType, ushort seq, byte[] payload)
    {
        var packet = new byte[12 + payload.Length];
        packet[0] = 0x80;
        packet[1] = payloadType;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), seq);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), ssrc);
        payload.CopyTo(packet.AsSpan(12));
        return packet;
    }

    private static byte[] Rtcp(uint ssrc, int payloadLength)
    {
        var packet = new byte[8 + payloadLength];
        packet[0] = 0x81; // V=2, RC=1
        packet[1] = 200;  // PT = SR
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)((packet.Length / 4) - 1));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), ssrc);
        for (var i = 8; i < packet.Length; i++)
            packet[i] = (byte)(0xA0 + i);
        return packet;
    }
}
