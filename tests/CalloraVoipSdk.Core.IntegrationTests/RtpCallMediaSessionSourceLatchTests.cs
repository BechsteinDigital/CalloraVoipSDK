using System.Collections.Concurrent;
using System.Diagnostics;
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
/// A SIP audio leg carries exactly one remote synchronisation source (#161 P2-6). The inbound processor
/// validates up to 64 SSRCs separately, but the jitter buffer, playout cursor, concealment state and
/// receiver-report bookkeeping behind it are single-stream — so a second source arriving at the same time
/// would interleave two sequence and timestamp spaces in one buffer. The leg latches its source and drops the
/// rest, while a genuine source change (a media server switching legs, a peer reseeding after an SSRC
/// collision) still takes over once the latched source has gone quiet.
/// </summary>
public sealed class RtpCallMediaSessionSourceLatchTests
{
    private const uint LatchedSsrc = 0x1111_1111;
    private const uint OtherSsrc = 0x2222_2222;

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

    // Timestamps follow the wall clock at 8 kHz so a packet's playout slot is always still ahead of its
    // arrival; the payload is stamped with a per-source marker so delivery can be attributed.
    private static async Task SendAsync(
        UdpClient peer, IPEndPoint target, Stopwatch clock, uint ssrc, ushort sequenceNumber, byte marker)
    {
        var payload = new byte[160];
        Array.Fill(payload, marker);
        var datagram = new RtpPacketCodec().Encode(new RtpPacket
        {
            PayloadType = 0,
            SequenceNumber = sequenceNumber,
            Timestamp = (uint)(clock.ElapsedMilliseconds * 8),
            Ssrc = ssrc,
            Payload = payload,
        });
        await peer.SendAsync(datagram, datagram.Length, target);
    }

    [Fact]
    public async Task A_second_concurrent_source_is_dropped_not_mixed_into_the_playout()
    {
        var localPort = FreeUdpPort();
        await using var session = CreateSession(localPort);

        var delivered = new ConcurrentQueue<byte>();
        session.FrameReceived += frame => delivered.Enqueue(frame.Payload[0]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await session.StartAsync(cts.Token);

        using var peer = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, localPort);
        var clock = Stopwatch.StartNew();

        // Both sources send throughout, so the latched one never goes quiet and the other never takes over.
        for (ushort i = 0; i < 8; i++)
        {
            await SendAsync(peer, target, clock, LatchedSsrc, (ushort)(100 + i), 0x0A);
            await SendAsync(peer, target, clock, OtherSsrc, (ushort)(9000 + i), 0x0B);
            await Task.Delay(20, cts.Token);
        }

        while (delivered.Count < 8)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
        await Task.Delay(200, cts.Token);

        Assert.All(delivered, marker => Assert.Equal(0x0A, marker));
        Assert.Equal(8, session.ForeignSourcePacketsDropped);
    }

    [Fact]
    public async Task A_new_source_takes_over_once_the_latched_one_has_gone_quiet()
    {
        var localPort = FreeUdpPort();
        await using var session = CreateSession(localPort);

        var delivered = new ConcurrentQueue<byte>();
        session.FrameReceived += frame => delivered.Enqueue(frame.Payload[0]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await session.StartAsync(cts.Token);

        using var peer = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, localPort);
        var clock = Stopwatch.StartNew();

        for (ushort i = 0; i < 5; i++)
        {
            await SendAsync(peer, target, clock, LatchedSsrc, (ushort)(100 + i), 0x0A);
            await Task.Delay(20, cts.Token);
        }

        while (delivered.Count < 5)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }

        // The old source stops; after the takeover idle the new one establishes itself over consecutive packets.
        await Task.Delay(600, cts.Token);
        for (ushort i = 0; i < 20; i++)
        {
            await SendAsync(peer, target, clock, OtherSsrc, (ushort)(9000 + i), 0x0B);
            await Task.Delay(20, cts.Token);
        }

        while (!delivered.Contains((byte)0x0B))
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }

        // The first packets of the new source are still refused (the takeover needs a run of them), and the
        // stream state is reset on takeover — so the switch costs a few packets, not the rest of the call.
        Assert.InRange(session.ForeignSourcePacketsDropped, 1, 15);
    }

    // The latch itself, on an injected clock: the takeover window is asserted exactly instead of waited out.

    [Fact]
    public void Interleaved_foreign_packets_never_take_over_however_many_arrive()
    {
        var now = DateTimeOffset.UnixEpoch;
        var latch = new RtpRemoteSourceLatch(NullLogger.Instance, () => now);

        Assert.True(latch.Admit(LatchedSsrc, out _));

        // The other source sends twice as much, but the latched one keeps interrupting the streak — and the
        // idle window never opens either.
        for (var i = 0; i < 100; i++)
        {
            now += TimeSpan.FromMilliseconds(20);
            Assert.False(latch.Admit(OtherSsrc, out _));
            Assert.False(latch.Admit(OtherSsrc, out _));
            Assert.True(latch.Admit(LatchedSsrc, out var changed));
            Assert.False(changed);
        }

        Assert.Equal(LatchedSsrc, latch.LatchedSource);
        Assert.Equal(200, latch.DroppedPackets);
    }

    [Fact]
    public void A_takeover_needs_both_the_packet_run_and_the_idle_window()
    {
        var now = DateTimeOffset.UnixEpoch;
        var latch = new RtpRemoteSourceLatch(NullLogger.Instance, () => now);

        Assert.True(latch.Admit(LatchedSsrc, out _));

        // A full run of packets while the latched source is only just quiet: not yet.
        for (var i = 0; i < RtpRemoteSourceLatch.TakeoverPackets * 2; i++)
        {
            now += TimeSpan.FromMilliseconds(10); // stays inside the idle window
            Assert.False(latch.Admit(OtherSsrc, out _));
        }

        Assert.Equal(LatchedSsrc, latch.LatchedSource);

        // Past the idle window the run completes and the next packet of the same source takes over — once.
        now += RtpRemoteSourceLatch.TakeoverIdle;
        Assert.True(latch.Admit(OtherSsrc, out var changed));
        Assert.True(changed);
        Assert.Equal(OtherSsrc, latch.LatchedSource);

        Assert.True(latch.Admit(OtherSsrc, out var changedAgain));
        Assert.False(changedAgain);
    }
}
