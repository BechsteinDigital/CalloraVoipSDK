using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Retransmission;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RTX retransmission end to end (WebRTC phase 3, RFC 4585 + RFC 4588): a video sender
/// buffers its sent packets, and on an inbound Generic NACK resends the requested packet on
/// the RTX repair stream (own payload type and SSRC, OSN prefix) — proven over UDP loopback.
/// </summary>
public sealed class VideoRtxRetransmitE2eTests
{
    private const byte VideoPt = 96;
    private const byte RtxPt = 98;
    private static readonly RtcpPacketCodec RtcpCodec = new();
    private static readonly RtpPacketCodec RtpCodec = new();

    [Fact]
    public async Task Inbound_nack_retransmits_the_requested_packet_as_rtx()
    {
        var localVideoPort = FreeUdpPort();
        var peerPort = FreeUdpPort();
        await using var sender = CreateSession(localVideoPort, peerPort);
        await sender.StartAsync();

        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));
        var senderTarget = new IPEndPoint(IPAddress.Loopback, localVideoPort);

        // The sender emits a couple of video frames; we capture the wire packet for seq we
        // will NACK so we can compare the retransmission's recovered payload against it.
        var payload = new byte[] { 0x10, 0xAA, 0xBB, 0xCC }; // VP8 descriptor + data
        await sender.Video!.SendFrameAsync(payload, rtpTimestamp: 3000);
        await sender.Video!.SendFrameAsync(payload, rtpTimestamp: 6000);

        // Drain the two original video packets the peer received, note their sequence numbers.
        using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = RtpCodec.Decode((await peer.ReceiveAsync(drain.Token)).Buffer);
        _ = await peer.ReceiveAsync(drain.Token);

        // NACK the first packet's sequence number.
        var nack = RtcpCodec.Encode([new RtcpGenericNack
        {
            SenderSsrc = 0xBEEF,
            MediaSsrc = first.Ssrc,
            Entries = [new RtcpNackEntry { PacketId = first.SequenceNumber, LostPacketBitmask = 0 }],
        }]);
        await peer.SendAsync(nack, senderTarget);

        // The retransmission arrives on the RTX payload type with its own SSRC; decapsulating
        // it (RFC 4588 §4) recovers the original packet.
        using var receiveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        RtpPacket? recovered = null;
        while (recovered is null)
        {
            var datagram = (await peer.ReceiveAsync(receiveTimeout.Token)).Buffer;
            var rtp = RtpCodec.Decode(datagram);
            if (rtp.PayloadType != RtxPt)
                continue;

            Assert.NotEqual(first.Ssrc, rtp.Ssrc); // RTX uses its own SSRC
            Assert.True(RtxPacketFactory.TryDecapsulate(rtp, VideoPt, first.Ssrc, out recovered));
        }

        // The recovered packet equals the original the peer first received (the RTP payload
        // is the packetiser's VP8-descriptor + frame, not the raw frame).
        Assert.Equal(first.SequenceNumber, recovered!.SequenceNumber);
        Assert.Equal(VideoPt, recovered.PayloadType);
        Assert.Equal(first.Payload.ToArray(), recovered.Payload.ToArray());
    }

    [Fact]
    public async Task Multiple_nacked_packets_retransmit_with_ascending_rtx_sequence_numbers()
    {
        var localVideoPort = FreeUdpPort();
        var peerPort = FreeUdpPort();
        await using var sender = CreateSession(localVideoPort, peerPort);
        await sender.StartAsync();

        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));
        var senderTarget = new IPEndPoint(IPAddress.Loopback, localVideoPort);

        var payload = new byte[] { 0x10, 0x01, 0x02, 0x03 };
        for (var i = 0; i < 4; i++)
            await sender.Video!.SendFrameAsync(payload, rtpTimestamp: (uint)((i + 1) * 3000));

        using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var originals = new List<RtpPacket>();
        for (var i = 0; i < 4; i++)
            originals.Add(RtpCodec.Decode((await peer.ReceiveAsync(drain.Token)).Buffer));

        // NACK the first packet plus the two after it (bitmask bits 0 and 1) → three resends.
        await peer.SendAsync(RtcpCodec.Encode([new RtcpGenericNack
        {
            SenderSsrc = 0xBEEF,
            MediaSsrc = originals[0].Ssrc,
            Entries = [new RtcpNackEntry { PacketId = originals[0].SequenceNumber, LostPacketBitmask = 0b11 }],
        }]), senderTarget);

        using var receiveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var rtxSequences = new List<ushort>();
        while (rtxSequences.Count < 3)
        {
            var rtp = RtpCodec.Decode((await peer.ReceiveAsync(receiveTimeout.Token)).Buffer);
            if (rtp.PayloadType == RtxPt)
                rtxSequences.Add(rtp.SequenceNumber);
        }

        // The RTX stream numbers its own packets monotonically, independent of the originals.
        Assert.Equal(rtxSequences.OrderBy(s => s).ToArray(), rtxSequences.ToArray());
        Assert.Equal(3, rtxSequences.Distinct().Count());
    }

    [Fact]
    public async Task Nack_for_a_packet_never_sent_produces_no_retransmission()
    {
        var localVideoPort = FreeUdpPort();
        var peerPort = FreeUdpPort();
        await using var sender = CreateSession(localVideoPort, peerPort);
        await sender.StartAsync();

        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));
        var nack = RtcpCodec.Encode([new RtcpGenericNack
        {
            SenderSsrc = 0xBEEF,
            MediaSsrc = 0x1234,
            Entries = [new RtcpNackEntry { PacketId = 40000, LostPacketBitmask = 0 }],
        }]);
        await peer.SendAsync(nack, new IPEndPoint(IPAddress.Loopback, localVideoPort));

        // Nothing was ever sent, so the buffer is empty and no RTX goes out.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await peer.ReceiveAsync(timeout.Token));
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
                PayloadType = VideoPt,
                CodecName = "VP8",
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localVideoPort),
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, remoteVideoPort),
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
