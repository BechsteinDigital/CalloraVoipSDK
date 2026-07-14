using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Video RTCP feedback end to end (WebRTC phase 3): a real <see cref="RtpCallMediaSession"/>
/// video stream over UDP loopback raises <c>KeyFrameRequested</c> on an inbound PLI, and on
/// a detected sequence gap sends both a Generic NACK naming the missing packet and a PLI to
/// the peer (RFC 4585 §6.2.1 / §6.3.1) when the peer advertised those feedback types.
/// </summary>
public sealed class VideoFeedbackE2eTests
{
    private static readonly RtcpPacketCodec RtcpCodec = new();
    private static readonly RtpPacketCodec RtpCodec = new();

    [Fact]
    public async Task Inbound_pli_raises_keyframe_request_on_the_video_stream()
    {
        var localVideoPort = FreeUdpPort();
        await using var session = CreateSession(localVideoPort, remoteVideoPort: FreeUdpPort());

        var keyframeRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Video!.KeyFrameRequested += () => keyframeRequested.TrySetResult();
        await session.StartAsync();

        using var peer = new UdpClient();
        var pli = RtcpCodec.Encode([new RtcpPictureLossIndication { SenderSsrc = 0x1, MediaSsrc = 0x2 }]);
        await peer.SendAsync(pli, new IPEndPoint(IPAddress.Loopback, localVideoPort));

        await keyframeRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Detected_sequence_gap_sends_a_pli_to_the_peer()
    {
        var localVideoPort = FreeUdpPort();
        var peerPort = FreeUdpPort();
        await using var session = CreateSession(localVideoPort, peerPort);
        await session.StartAsync();

        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));
        const uint peerSsrc = 0x0BADF00D;
        var target = new IPEndPoint(IPAddress.Loopback, localVideoPort);

        // First a delivered packet establishes the receive baseline, then a gap (seq +2)
        // must make the stream ask the peer for a keyframe.
        await peer.SendAsync(VideoRtpPacket(seq: 100, peerSsrc), target);
        await Task.Delay(50);
        await peer.SendAsync(VideoRtpPacket(seq: 102, peerSsrc), target);

        using var receiveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        RtcpPictureLossIndication? pli = null;
        while (pli is null)
        {
            var datagram = (await peer.ReceiveAsync(receiveTimeout.Token)).Buffer;
            pli = RtcpCodec.Decode(datagram).OfType<RtcpPictureLossIndication>().FirstOrDefault();
        }

        Assert.Equal(peerSsrc, pli.MediaSsrc);
    }

    [Fact]
    public async Task Detected_sequence_gap_sends_a_nack_for_the_missing_packet()
    {
        var localVideoPort = FreeUdpPort();
        var peerPort = FreeUdpPort();
        await using var session = CreateSession(localVideoPort, peerPort);
        await session.StartAsync();

        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));
        const uint peerSsrc = 0x0BADF00D;
        var target = new IPEndPoint(IPAddress.Loopback, localVideoPort);

        // seq 100 delivered, then 102 → 101 is missing.
        await peer.SendAsync(VideoRtpPacket(seq: 100, peerSsrc), target);
        await Task.Delay(50);
        await peer.SendAsync(VideoRtpPacket(seq: 102, peerSsrc), target);

        using var receiveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        RtcpGenericNack? nack = null;
        while (nack is null)
        {
            var datagram = (await peer.ReceiveAsync(receiveTimeout.Token)).Buffer;
            nack = RtcpCodec.Decode(datagram).OfType<RtcpGenericNack>().FirstOrDefault();
        }

        Assert.Equal(peerSsrc, nack.MediaSsrc);
        Assert.Equal((ushort[])[101], nack.LostSequenceNumbers().ToArray());
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static RtpCallMediaSession CreateSession(int localVideoPort, int remoteVideoPort)
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
                PayloadType = 96,
                CodecName = "VP8",
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localVideoPort),
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, remoteVideoPort),
                RemoteSupportsNack = true,
                RemoteSupportsPli = true,
            },
        };

        return new RtpCallMediaSession(parameters, NullLoggerFactory.Instance);
    }

    private static byte[] VideoRtpPacket(ushort seq, uint ssrc)
    {
        // Minimal VP8 payload (S=1/PID=0 descriptor + one byte); marker closes the frame.
        return RtpCodec.Encode(new RtpPacket
        {
            PayloadType = 96,
            Marker = true,
            SequenceNumber = seq,
            Timestamp = (uint)(seq * 3000),
            Ssrc = ssrc,
            Payload = new byte[] { 0x10, 0xAA },
        });
    }

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
