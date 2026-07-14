using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Retransmission;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RTX retransmission receive end to end (WebRTC phase 3, RFC 4585 + RFC 4588): a video
/// receiver feeds inbound video through a reorder window, and an RTX repair packet delivered
/// on the secondary stream fills the gap of a dropped packet — so the recovered frame is
/// depacketised and surfaced in sequence order. Proven over UDP loopback with a hand-crafted
/// peer that deliberately drops one packet and then retransmits it as RTX.
/// </summary>
public sealed class VideoRtxReceiveE2eTests
{
    private const byte VideoPt = 96;
    private const byte RtxPt = 98;
    private const uint RemoteMediaSsrc = 0x0A0B0C0D;
    private static readonly RtpPacketCodec Codec = new();
    private static readonly Vp8Packetiser Packetiser = new();

    [Fact]
    public async Task Dropped_video_packet_recovered_via_rtx_is_delivered_in_order()
    {
        var receiverPort = FreeUdpPort();
        var peerPort = FreeUdpPort();
        await using var receiver = CreateReceiver(receiverPort, peerPort);

        var delivered = new List<int>();
        var gate = new object();
        receiver.Video!.FrameReceived += (frame, _) =>
        {
            lock (gate) delivered.Add(frame[0]); // frame[0] is the frame's id
        };
        await receiver.StartAsync();

        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));
        var target = new IPEndPoint(IPAddress.Loopback, receiverPort);

        // Send frames 1 and 2, drop 3, but retransmit 3 on the RTX repair stream, then keep
        // the stream flowing so the reorder window slides and releases in order.
        await SendVideo(peer, target, 1);
        await SendVideo(peer, target, 2);
        await SendRtx(peer, target, originalSeq: 3, rtxSeq: 1);
        for (ushort seq = 4; seq <= 64; seq++)
            await SendVideo(peer, target, seq);

        // The recovered frame 3 must appear, and everything delivered is in ascending order.
        await WaitUntil(() => { lock (gate) return delivered.Contains(3); });

        lock (gate)
        {
            Assert.Contains(3, delivered);
            Assert.Contains(2, delivered);
            Assert.Contains(4, delivered);
            Assert.Equal(delivered.OrderBy(id => id).ToArray(), delivered.ToArray());
        }
    }

    [Fact]
    public async Task Dropped_video_packet_without_rtx_surfaces_as_a_gap()
    {
        var receiverPort = FreeUdpPort();
        var peerPort = FreeUdpPort();
        await using var receiver = CreateReceiver(receiverPort, peerPort);

        var delivered = new List<int>();
        var gate = new object();
        receiver.Video!.FrameReceived += (frame, _) =>
        {
            lock (gate) delivered.Add(frame[0]);
        };
        await receiver.StartAsync();

        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));
        var target = new IPEndPoint(IPAddress.Loopback, receiverPort);

        // Drop 3 and never retransmit it: once more than the reorder depth of packets pile up
        // behind the gap, the window gives up and delivery skips it. The 4..64 burst is sized to
        // exceed that depth so the skip is guaranteed to fire.
        await SendVideo(peer, target, 1);
        await SendVideo(peer, target, 2);
        for (ushort seq = 4; seq <= 64; seq++)
            await SendVideo(peer, target, seq);

        await WaitUntil(() => { lock (gate) return delivered.Contains(4); });

        lock (gate)
        {
            Assert.Contains(2, delivered);
            Assert.Contains(4, delivered);
            Assert.DoesNotContain(3, delivered);
            Assert.Equal(delivered.OrderBy(id => id).ToArray(), delivered.ToArray());
        }
    }

    private static async Task SendVideo(UdpClient peer, IPEndPoint target, ushort seq)
    {
        var payloads = Packetiser.Packetise(MakeFrame(seq), 1200);
        var packet = new RtpPacket
        {
            PayloadType = VideoPt,
            SequenceNumber = seq,
            Timestamp = seq * 3000u,
            Marker = payloads[0].IsLastOfFrame,
            Ssrc = RemoteMediaSsrc,
            Payload = payloads[0].Payload,
        };
        await peer.SendAsync(Codec.Encode(packet), target);
        await Task.Delay(3);
    }

    private static async Task SendRtx(UdpClient peer, IPEndPoint target, ushort originalSeq, ushort rtxSeq)
    {
        var payloads = Packetiser.Packetise(MakeFrame(originalSeq), 1200);
        var original = new RtpPacket
        {
            PayloadType = VideoPt,
            SequenceNumber = originalSeq,
            Timestamp = originalSeq * 3000u,
            Marker = payloads[0].IsLastOfFrame,
            Ssrc = RemoteMediaSsrc,
            Payload = payloads[0].Payload,
        };
        var rtx = RtxPacketFactory.Encapsulate(original, RtxPt, rtxSsrc: 0x0BADF00D, rtxSequenceNumber: rtxSeq);
        await peer.SendAsync(Codec.Encode(rtx), target);
        await Task.Delay(3);
    }

    // A small VP8 frame that fits one RTP packet; frame[0] carries the id so delivery can be
    // identified, the rest is arbitrary filler that round-trips through packetise/depacketise.
    private static byte[] MakeFrame(ushort id)
    {
        var frame = new byte[40];
        frame[0] = (byte)id;
        for (var i = 1; i < frame.Length; i++)
            frame[i] = (byte)(i * 5 + id);
        return frame;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, deadline.Token);
        }
    }

    private static RtpCallMediaSession CreateReceiver(int localVideoPort, int peerVideoPort)
    {
        var parameters = new CallMediaParameters
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
            PayloadType = 0,
            ClockRate = 8000,
            SamplesPerPacket = 160,
            Video = new CallVideoParameters
            {
                PayloadType = VideoPt,
                CodecName = "VP8",
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localVideoPort),
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, peerVideoPort),
                RtxPayloadType = RtxPt,
                RemoteSupportsNack = true,
            },
        };

        return new RtpCallMediaSession(parameters, NullLoggerFactory.Instance);
    }

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
