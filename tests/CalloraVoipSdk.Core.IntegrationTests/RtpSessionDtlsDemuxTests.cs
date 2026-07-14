using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RFC 5764 §5.1.2 / RFC 7983 demultiplexing on the media socket: datagrams whose first
/// byte is in 20..63 are DTLS records and must be routed to the DTLS handler — never to
/// the RTP or RTCP paths.
/// </summary>
public sealed class RtpSessionDtlsDemuxTests
{
    [Fact]
    public async Task DtlsRecord_IsRoutedToDtlsHandler()
    {
        var localPort = FreeUdpPort();
        await using var session = new RtpSession(
            new RtpSessionOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
                PayloadType = 0,
                ClockRate = 8000,
                SamplesPerPacket = 160,
            },
            new RtpPacketCodec(), NullLogger<RtpSession>.Instance);

        var dtlsReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.DtlsPacketReceived += (datagram, _) => dtlsReceived.TrySetResult(datagram);
        var rtpReceived = false;
        session.PacketReceived += (_, _) => rtpReceived = true;

        await session.StartAsync();

        // Minimal DTLS record shape: content type 22 (handshake), version, epoch,
        // sequence number, zero-length fragment — 13 header bytes.
        var dtlsRecord = new byte[13];
        dtlsRecord[0] = 22;
        dtlsRecord[1] = 0xFE;
        dtlsRecord[2] = 0xFD;

        using var sender = new UdpClient();
        await sender.SendAsync(dtlsRecord, new IPEndPoint(IPAddress.Loopback, localPort));

        var routed = await dtlsReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(dtlsRecord, routed);
        Assert.False(rtpReceived);
    }

    [Fact]
    public async Task ShortLowByteDatagram_IsNotRoutedToDtlsHandler()
    {
        var localPort = FreeUdpPort();
        await using var session = new RtpSession(
            new RtpSessionOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
                PayloadType = 0,
                ClockRate = 8000,
                SamplesPerPacket = 160,
            },
            new RtpPacketCodec(), NullLogger<RtpSession>.Instance);

        var dtlsSeen = 0;
        session.DtlsPacketReceived += (_, _) => Interlocked.Increment(ref dtlsSeen);

        await session.StartAsync();

        using var sender = new UdpClient();
        // First byte in DTLS range but shorter than a record header — must be ignored.
        await sender.SendAsync(new byte[] { 22, 0xFE, 0xFD }, new IPEndPoint(IPAddress.Loopback, localPort));
        // First byte outside the DTLS range (RTP-like) — must not hit the DTLS path.
        var rtpLike = new byte[16];
        rtpLike[0] = 0x80;
        await sender.SendAsync(rtpLike, new IPEndPoint(IPAddress.Loopback, localPort));

        await Task.Delay(300);
        Assert.Equal(0, dtlsSeen);
    }

    [Fact]
    public async Task DemuxBoundaries_OnlyFirstByte20To63_IsDtls()
    {
        var localPort = FreeUdpPort();
        await using var session = new RtpSession(
            new RtpSessionOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
                PayloadType = 0,
                ClockRate = 8000,
                SamplesPerPacket = 160,
            },
            new RtpPacketCodec(), NullLogger<RtpSession>.Instance);

        var seenFirstBytes = new System.Collections.Concurrent.ConcurrentBag<byte>();
        var bothBoundaryRecords = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.DtlsPacketReceived += (datagram, _) =>
        {
            seenFirstBytes.Add(datagram[0]);
            if (seenFirstBytes.Count >= 2)
                bothBoundaryRecords.TrySetResult();
        };

        await session.StartAsync();

        using var sender = new UdpClient();
        // RFC 5764 §5.1.2 boundaries: 19 and 64 are outside the DTLS range, 20 and 63 inside.
        foreach (var firstByte in new byte[] { 19, 20, 63, 64 })
        {
            var datagram = new byte[13];
            datagram[0] = firstByte;
            await sender.SendAsync(datagram, new IPEndPoint(IPAddress.Loopback, localPort));
        }

        await bothBoundaryRecords.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200); // Grace period for any misrouted 19/64 datagram to surface.

        Assert.Equal(new byte[] { 20, 63 }, seenFirstBytes.OrderBy(b => b).ToArray());
    }

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
