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

    // FreeUdpPort() liest den Port über einen sofort geschlossenen Probe-Socket — zwischen Probe und
    // dem echten Bind (Session-Start / Peer-Bind) kann ein parallel laufender Test denselben Port
    // belegen (TOCTOU → SocketError.AddressAlreadyInUse). Auf dem geteilten CI-Runner (ubuntu) trat das
    // intermittierend auf. Der Aufbau wird daher bei EADDRINUSE mit frischen Ports wiederholt — analog
    // zu RtpMediaLoopback.StartAsync im Soak-Harness.
    private const int PortBindAttempts = 8;

    [Fact]
    public async Task Inbound_pli_raises_keyframe_request_on_the_video_stream()
    {
        var setup = await StartSessionWithRetryAsync();
        await using var session = setup.Session;

        var keyframeRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Video!.KeyFrameRequested += () => keyframeRequested.TrySetResult();

        using var peer = new UdpClient();
        var pli = RtcpCodec.Encode([new RtcpPictureLossIndication { SenderSsrc = 0x1, MediaSsrc = 0x2 }]);
        await peer.SendAsync(pli, new IPEndPoint(IPAddress.Loopback, setup.LocalVideoPort));

        await keyframeRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Detected_sequence_gap_sends_a_pli_to_the_peer()
    {
        var setup = await StartSessionWithBoundPeerAsync();
        await using var session = setup.Session;
        using var peer = setup.Peer;

        const uint peerSsrc = 0x0BADF00D;
        var target = new IPEndPoint(IPAddress.Loopback, setup.LocalVideoPort);

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
        var setup = await StartSessionWithBoundPeerAsync();
        await using var session = setup.Session;
        using var peer = setup.Peer;

        const uint peerSsrc = 0x0BADF00D;
        var target = new IPEndPoint(IPAddress.Loopback, setup.LocalVideoPort);

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

    /// <summary>
    /// Baut eine gestartete Video-Session (Peer bindet keinen festen Port). Wiederholt bei
    /// <see cref="SocketError.AddressAlreadyInUse"/> mit frischen Ports (TOCTOU-Absicherung).
    /// </summary>
    private static async Task<(RtpCallMediaSession Session, int LocalVideoPort)> StartSessionWithRetryAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            var localVideoPort = FreeUdpPort();
            RtpCallMediaSession? session = null;
            try
            {
                session = CreateSession(localVideoPort, remoteVideoPort: FreeUdpPort());
                await session.StartAsync();
                return (session, localVideoPort);
            }
            catch (SocketException ex)
                when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && attempt < PortBindAttempts)
            {
                if (session is not null)
                    await session.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Baut eine gestartete Video-Session samt Peer, der auf <c>peerPort</c> bindet. Wiederholt bei
    /// <see cref="SocketError.AddressAlreadyInUse"/> mit frischen Ports (TOCTOU-Absicherung).
    /// </summary>
    private static async Task<(RtpCallMediaSession Session, UdpClient Peer, int LocalVideoPort)> StartSessionWithBoundPeerAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            var localVideoPort = FreeUdpPort();
            var peerPort = FreeUdpPort();
            RtpCallMediaSession? session = null;
            UdpClient? peer = null;
            try
            {
                session = CreateSession(localVideoPort, peerPort);
                await session.StartAsync();
                peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));
                return (session, peer, localVideoPort);
            }
            catch (SocketException ex)
                when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && attempt < PortBindAttempts)
            {
                peer?.Dispose();
                if (session is not null)
                    await session.DisposeAsync();
            }
        }
    }

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
