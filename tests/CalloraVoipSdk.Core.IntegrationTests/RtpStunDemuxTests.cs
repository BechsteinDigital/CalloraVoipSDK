using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Verifies the STUN/RTP demux seam on the shared media socket (RFC 7983 / RFC 5764 §5.1.2,
/// RFC 8445 §7.3): STUN connectivity checks arriving on the RTP 5-tuple are routed to the
/// <see cref="RtpSession.StunPacketReceived"/> hook with the sender address and never reach the
/// RTP path, non-STUN datagrams stay on the RTP path, and <see cref="RtpSession.SendRawAsync"/>
/// sends STUN responses on the same socket to an explicit destination.
/// </summary>
public sealed class RtpStunDemuxTests
{
    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static RtpSessionOptions Options(int localPort, int remotePort) => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, remotePort),
        PayloadType = 0,
        ClockRate = 8000,
        SamplesPerPacket = 160,
    };

    private static byte[] StunBindingRequest()
        => new StunMessageCodec().Encode(StunMessage.CreateBindingRequest());

    [Fact]
    public async Task Stun_datagram_is_routed_to_stun_hook_and_not_to_rtp()
    {
        var sessionPort = FreeUdpPort();
        var senderPort = FreeUdpPort();

        await using var session = new RtpSession(
            Options(sessionPort, senderPort), new RtpPacketCodec(), NullLogger<RtpSession>.Instance);

        var stunHits = new List<(byte[] Datagram, IPEndPoint Source)>();
        var gate = new object();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rtpHits = 0;

        session.StunPacketReceived += (datagram, source) =>
        {
            lock (gate) stunHits.Add((datagram, source));
            received.TrySetResult();
        };
        session.PacketReceived += (_, _) => Interlocked.Increment(ref rtpHits);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await session.StartAsync(cts.Token);

        var stun = StunBindingRequest();
        using (var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, senderPort)))
            await sender.SendAsync(stun, new IPEndPoint(IPAddress.Loopback, sessionPort), cts.Token);

        await received.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        var hit = Assert.Single(stunHits);
        Assert.Equal(stun, hit.Datagram);
        Assert.Equal(senderPort, hit.Source.Port);
        Assert.Equal(0, Volatile.Read(ref rtpHits));
    }

    [Fact]
    public async Task Rtp_and_low_byte_non_cookie_datagrams_do_not_trigger_the_stun_hook()
    {
        var sessionPort = FreeUdpPort();
        var senderPort = FreeUdpPort();

        await using var session = new RtpSession(
            Options(sessionPort, senderPort), new RtpPacketCodec(), NullLogger<RtpSession>.Instance);

        var stunHits = new List<byte[]>();
        var gate = new object();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        session.StunPacketReceived += (datagram, _) =>
        {
            lock (gate) stunHits.Add(datagram);
            received.TrySetResult();
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await session.StartAsync(cts.Token);

        var target = new IPEndPoint(IPAddress.Loopback, sessionPort);
        // Same source socket → loopback preserves ordering, so the STUN packet arriving at the
        // hook proves the two earlier datagrams were processed first without firing it.
        using (var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, senderPort)))
        {
            // RTP-shaped: version 2 (first byte 0x80), payload type 0.
            await sender.SendAsync(new byte[] { 0x80, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0, 0, 0 }, target, cts.Token);
            // Low first byte but no STUN magic cookie at offset 4.
            await sender.SendAsync(new byte[] { 0x00, 0x01, 0x00, 0x00, 0xDE, 0xAD, 0xBE, 0xEF, 0, 0, 0, 0 }, target, cts.Token);
            // Real STUN Binding request — must be the only one demuxed to the hook.
            await sender.SendAsync(StunBindingRequest(), target, cts.Token);
        }

        await received.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        var only = Assert.Single(stunHits);
        // Confirm it is the STUN packet (magic cookie 0x2112A442 at offset 4), not the decoy.
        Assert.Equal(new byte[] { 0x21, 0x12, 0xA4, 0x42 }, only.AsSpan(4, 4).ToArray());
    }

    [Fact]
    public async Task SendRawAsync_sends_to_the_explicit_destination()
    {
        var sessionPort = FreeUdpPort();
        var remotePort = FreeUdpPort();     // the session's configured RemoteEndPoint (must NOT be used)
        var destinationPort = FreeUdpPort();

        await using var session = new RtpSession(
            Options(sessionPort, remotePort), new RtpPacketCodec(), NullLogger<RtpSession>.Instance);

        using var destination = new UdpClient(new IPEndPoint(IPAddress.Loopback, destinationPort));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var payload = new byte[] { 1, 2, 3, 4, 5 };
        await session.SendRawAsync(payload, new IPEndPoint(IPAddress.Loopback, destinationPort), cts.Token);

        var result = await destination.ReceiveAsync(cts.Token);
        Assert.Equal(payload, result.Buffer);
        Assert.Equal(sessionPort, result.RemoteEndPoint.Port); // arrived from the media socket
    }
}
