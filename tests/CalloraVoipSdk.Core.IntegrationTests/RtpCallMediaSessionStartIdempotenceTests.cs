using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Application.Media.Sessions;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// <c>RtpCallMediaSession.StartAsync</c> is idempotent (#161 P2-8). A repeated start used to spin up a
/// second playout loop and overwrite the handle to the first, leaving an orphan running against the same
/// jitter buffer and the same unsynchronised delivery state — and leaving DisposeAsync able to await only
/// the last one. Starting after disposal is a no-op as well.
/// </summary>
public sealed class RtpCallMediaSessionStartIdempotenceTests
{
    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static RtpCallMediaSession CreateSession(int localPort)
        => (RtpCallMediaSession)new RtpCallMediaSessionFactory(NullLoggerFactory.Instance, PayloadCodecKind.Pcmu)
            .Create(new CallMediaParameters
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
                PayloadType = 0,
                CodecName = "PCMU",
                ClockRate = 8000,
                SamplesPerPacket = 160,
            });

    [Fact]
    public async Task A_second_start_keeps_the_running_playout_loop()
    {
        await using var session = CreateSession(FreeUdpPort());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await session.StartAsync(cts.Token);
        var loop = session.PlayoutLoopForTest;
        Assert.NotNull(loop);

        await session.StartAsync(cts.Token);
        await session.StartAsync(cts.Token);

        Assert.Same(loop, session.PlayoutLoopForTest);
    }

    [Fact]
    public async Task A_restarted_session_still_delivers_each_packet_once()
    {
        var localPort = FreeUdpPort();
        await using var session = CreateSession(localPort);

        var delivered = new ConcurrentQueue<byte>();
        session.FrameReceived += frame => delivered.Enqueue(frame.Payload[0]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await session.StartAsync(cts.Token);
        await session.StartAsync(cts.Token);

        using var peer = new UdpClient();
        var codec = new RtpPacketCodec();
        var target = new IPEndPoint(IPAddress.Loopback, localPort);
        for (ushort seq = 1; seq <= 5; seq++)
        {
            var payload = new byte[160];
            Array.Fill(payload, (byte)seq);
            var datagram = codec.Encode(new RtpPacket
            {
                PayloadType = 0,
                SequenceNumber = seq,
                Timestamp = (uint)(seq * 160),
                Ssrc = 0x2222,
                Payload = payload,
            });
            await peer.SendAsync(datagram, datagram.Length, target);
            await Task.Delay(20, cts.Token);
        }

        while (delivered.Count < 5)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
        await Task.Delay(150, cts.Token); // a duplicate delivery would show up here

        Assert.Equal([(byte)1, 2, 3, 4, 5], delivered.ToArray());
    }

    [Fact]
    public async Task Starting_after_disposal_is_a_no_op()
    {
        var session = CreateSession(FreeUdpPort());
        await session.StartAsync();
        await session.DisposeAsync();

        var thrown = await Record.ExceptionAsync(() => session.StartAsync());

        Assert.Null(thrown);
    }
}
