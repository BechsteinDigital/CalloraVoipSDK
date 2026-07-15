using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Sender-side transport-cc congestion control wired end to end (WebRTC phase 3): a real
/// <see cref="RtpCallMediaSession"/> video stream stamps its outgoing packets, a peer feeds back a
/// report showing strongly growing one-way delay, and the stream's congestion controller registers
/// overuse. Confirms the VideoRtpStream → TransportCcCongestionController wiring
/// (PacketSent → send history, ControlPacketReceived → estimators) is live.
/// </summary>
public sealed class TransportCcCongestionWiringE2eTests
{
    private const byte TransportCcExtId = 5;
    private static readonly RtcpPacketCodec RtcpCodec = new();
    private static readonly RtpPacketCodec RtpCodec = new();

    [Fact]
    public async Task Feedback_showing_growing_delay_drives_the_sender_signal_to_overusing()
    {
        var localVideoPort = FreeUdpPort();
        var peerPort = FreeUdpPort();
        await using var session = CreateSession(localVideoPort, peerPort);
        await session.StartAsync();

        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));
        var target = new IPEndPoint(IPAddress.Loopback, localVideoPort);

        // 1. Send video frames quickly → stamped packets go out (send times recorded µs apart).
        for (var i = 0; i < 8; i++)
            await session.Video!.SendFrameAsync(new byte[100], rtpTimestamp: (uint)(i * 3000));

        // 2. Peer receives them and reads the transport-wide sequence numbers, in send order.
        var sequenceNumbers = new List<ushort>();
        using (var receiveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while (sequenceNumbers.Count < 4)
            {
                var datagram = (await peer.ReceiveAsync(receiveTimeout.Token)).Buffer;
                var packet = RtpCodec.Decode(datagram);
                if (OneByteRtpHeaderExtensions.TryReadTransportSequenceNumber(
                        packet.HeaderExtension, TransportCcExtId, out var seq))
                    sequenceNumbers.Add(seq);
            }
        }

        // 3. Build a report where each packet arrives 100 ms after the previous — far beyond the µs
        //    send spacing, so the delay gradient is strongly positive.
        var arrivals = sequenceNumbers
            .Select((seq, i) => new TransportCcArrival { SequenceNumber = seq, ArrivalTimestamp = i * 100_000L })
            .ToArray();
        var feedback = TransportCcFeedbackBuilder.Build(
            arrivals, senderSsrc: 0xAAAA, mediaSsrc: 0x1234, feedbackPacketCount: 0,
            epochTimestamp: 0, ticksPerSecond: 1_000_000);
        await peer.SendAsync(RtcpCodec.Encode([feedback]), target);

        // 4. The stream's sender-side controller should register overuse.
        var congestion = ((VideoRtpStream)session.Video!).Congestion!;
        using var pollTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (congestion.Signal != CongestionSignal.Overusing)
            await Task.Delay(25, pollTimeout.Token);

        Assert.Equal(CongestionSignal.Overusing, congestion.Signal);
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

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
