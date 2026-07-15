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
/// Transport-cc feedback wired end to end (WebRTC phase 3): a real <see cref="RtpCallMediaSession"/>
/// video stream over UDP loopback, on a leg that negotiated the transport-cc a=extmap, records
/// stamped inbound arrivals and sends a transport-cc RTCP report back to the peer. Confirms the
/// VideoRtpStream → TransportCcFeedbackSender wiring is live when the extension id is present.
/// </summary>
public sealed class TransportCcFeedbackWiringE2eTests
{
    private const byte TransportCcExtId = 5;
    private static readonly RtcpPacketCodec RtcpCodec = new();
    private static readonly RtpPacketCodec RtpCodec = new();

    [Fact]
    public async Task Stamped_inbound_video_makes_the_stream_send_transport_cc_feedback()
    {
        var localVideoPort = FreeUdpPort();
        var peerPort = FreeUdpPort();
        await using var session = CreateSession(localVideoPort, peerPort);
        await session.StartAsync();

        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));
        const uint peerSsrc = 0x0BADF00D;
        var target = new IPEndPoint(IPAddress.Loopback, localVideoPort);

        // Stamped video packets spread across more than one feedback interval (~100 ms) so the
        // receiver builds and sends a transport-cc report over the RTCP-mux channel.
        for (ushort i = 0; i < 6; i++)
        {
            await peer.SendAsync(
                StampedVideoRtpPacket(rtpSeq: (ushort)(100 + i), transportSeq: (ushort)(500 + i), peerSsrc),
                target);
            await Task.Delay(40);
        }

        using var receiveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        RtcpTransportFeedback? feedback = null;
        while (feedback is null)
        {
            var datagram = (await peer.ReceiveAsync(receiveTimeout.Token)).Buffer;
            feedback = RtcpCodec.Decode(datagram).OfType<RtcpTransportFeedback>().FirstOrDefault();
        }

        Assert.Equal(peerSsrc, feedback.MediaSsrc);
        Assert.Contains(feedback.Statuses, s => s.Received);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static RtpCallMediaSession CreateSession(int localVideoPort, int remoteVideoPort) =>
        new(new CallMediaParameters
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
                TransportWideCcExtensionId = TransportCcExtId,
            },
        }, NullLoggerFactory.Instance);

    private static byte[] StampedVideoRtpPacket(ushort rtpSeq, ushort transportSeq, uint ssrc) =>
        RtpCodec.Encode(new RtpPacket
        {
            PayloadType = 96,
            Marker = true,
            SequenceNumber = rtpSeq,
            Timestamp = (uint)(rtpSeq * 3000),
            Ssrc = ssrc,
            Payload = new byte[] { 0x10, 0xAA },
            HeaderExtension = OneByteRtpHeaderExtensions.Encode(
                [OneByteRtpHeaderExtensions.TransportSequenceNumber(TransportCcExtId, transportSeq)]),
        });

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
