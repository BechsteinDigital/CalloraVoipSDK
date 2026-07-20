using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// SSRC collision handling (CF-005, RFC 3550 §8.2): when a third party transmits with the session's own SSRC,
/// the session sends an RTCP BYE for the departing SSRC over the control path, adopts a fresh SSRC with a
/// re-seeded sequence/timestamp, and its subsequent RTP carries the new SSRC. A distinct inbound SSRC is not a
/// collision and triggers no BYE. Proven over real UDP loopback with plaintext RTCP (no SRTCP context).
/// </summary>
public sealed class RtpSessionSsrcCollisionTests
{
    private const byte Pt = 96;
    private const uint LocalSsrc = 0x1234_5678;
    private static readonly RtpPacketCodec RtpCodec = new();
    private static readonly RtcpPacketCodec RtcpCodec = new();

    [Fact]
    public async Task A_collision_sends_a_bye_for_the_old_ssrc_and_adopts_a_new_one()
    {
        var peerPort = FreeUdpPort();
        await using var session = CreateSession(FreeUdpPort(), peerPort);
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));

        var collisionRaised = 0;
        session.SsrcCollisionDetected += (_, _) => Interlocked.Increment(ref collisionRaised);

        // A third party transmits with our SSRC.
        session.InjectInboundDatagramForTest(Packet(seq: 1, ssrc: LocalSsrc, payload: [0xAA]));

        // The session re-seeded to a fresh SSRC and raised the event.
        Assert.NotEqual(LocalSsrc, session.LocalSsrc);
        Assert.Equal(1, Volatile.Read(ref collisionRaised));

        // A BYE for the departing (old) SSRC reaches the peer over the control path.
        var datagram = await ReceiveAsync(peer, TimeSpan.FromSeconds(5));
        var bye = Assert.Single(RtcpCodec.Decode(datagram).OfType<RtcpByePacket>());
        Assert.Equal(LocalSsrc, Assert.Single(bye.Sources));
        Assert.Equal("ssrc collision", bye.Reason);
    }

    [Fact]
    public async Task After_a_collision_the_next_rtp_send_carries_the_new_ssrc()
    {
        var peerPort = FreeUdpPort();
        await using var session = CreateSession(FreeUdpPort(), peerPort);
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));

        session.InjectInboundDatagramForTest(Packet(seq: 1, ssrc: LocalSsrc, payload: [0xAA]));
        var newSsrc = session.LocalSsrc;
        Assert.NotEqual(LocalSsrc, newSsrc);

        // Drain the BYE that the collision emitted, then send RTP and inspect its SSRC.
        _ = await ReceiveAsync(peer, TimeSpan.FromSeconds(5));
        await session.SendAsync(new byte[] { 0x01, 0x02 });

        RtpPacket sent;
        do
        {
            sent = RtpCodec.Decode(await ReceiveAsync(peer, TimeSpan.FromSeconds(5)));
        }
        while (sent.PayloadType is >= 192 and <= 223); // skip any trailing RTCP, keep the RTP packet

        Assert.Equal(newSsrc, sent.Ssrc);
        Assert.NotEqual(LocalSsrc, sent.Ssrc);
    }

    [Fact]
    public async Task A_distinct_inbound_ssrc_is_not_a_collision_and_sends_no_bye()
    {
        var peerPort = FreeUdpPort();
        await using var session = CreateSession(FreeUdpPort(), peerPort);
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));

        var collisionRaised = 0;
        var delivered = new TaskCompletionSource<RtpPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.SsrcCollisionDetected += (_, _) => Interlocked.Increment(ref collisionRaised);
        session.PacketReceived += (_, p) => delivered.TrySetResult(p);

        session.InjectInboundDatagramForTest(Packet(seq: 1, ssrc: 0x0BAD_F00D, payload: [0xAA]));

        var packet = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0x0BAD_F00Du, packet.Ssrc);
        Assert.Equal(LocalSsrc, session.LocalSsrc); // unchanged
        Assert.Equal(0, Volatile.Read(ref collisionRaised));

        // No control datagram is emitted for a normal remote source.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await peer.ReceiveAsync(timeout.Token));
    }

    private static RtpSession CreateSession(int localPort, int remotePort) =>
        new(new RtpSessionOptions
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, remotePort),
            PayloadType = Pt,
            ClockRate = 90000,
            SamplesPerPacket = 3000,
            Ssrc = LocalSsrc,
        },
        RtpCodec, NullLogger<RtpSession>.Instance);

    private static byte[] Packet(ushort seq, uint ssrc, byte[] payload) => RtpCodec.Encode(new RtpPacket
    {
        PayloadType = Pt,
        SequenceNumber = seq,
        Timestamp = (uint)(seq * 3000),
        Ssrc = ssrc,
        Payload = payload,
    });

    private static async Task<byte[]> ReceiveAsync(UdpClient socket, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return (await socket.ReceiveAsync(cts.Token)).Buffer;
    }

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
