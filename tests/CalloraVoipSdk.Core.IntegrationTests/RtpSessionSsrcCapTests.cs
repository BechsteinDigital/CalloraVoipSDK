using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Bounds gate for <see cref="RtpSession"/>'s per-SSRC validator table (HARD-A8). A source spoofing
/// a stream of distinct SSRCs must not grow the table without bound; it is capped and the
/// least-recently-active SSRC is evicted.
/// </summary>
public sealed class RtpSessionSsrcCapTests
{
    private const int MaxTrackedSsrcs = 64;
    private static readonly RtpPacketCodec Codec = new();

    private static RtpSession CreateSession()
        => new(
            new RtpSessionOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
                PayloadType = 96,
                ClockRate = 90000,
                SamplesPerPacket = 3000,
                Ssrc = 0xDEAD_0000, // fixed local SSRC, disjoint from the test's inbound SSRCs
            },
            Codec,
            NullLogger<RtpSession>.Instance);

    private static byte[] Packet(uint ssrc, ushort seq) => Codec.Encode(new RtpPacket
    {
        PayloadType = 96,
        SequenceNumber = seq,
        Timestamp = (uint)(seq * 3000),
        Ssrc = ssrc,
        Payload = new byte[] { 0x01 },
    });

    [Fact]
    public async Task Validator_table_is_capped_under_an_ssrc_flood()
    {
        await using var session = CreateSession();

        for (uint ssrc = 1; ssrc <= 200; ssrc++)
            session.InjectInboundDatagramForTest(Packet(ssrc, seq: 1));

        Assert.Equal(MaxTrackedSsrcs, session.TrackedSsrcCount);
    }

    [Fact]
    public async Task Least_recently_active_ssrc_is_evicted_first()
    {
        await using var session = CreateSession();

        // Fill the table to the cap: SSRC 1 first, then 2..64.
        for (uint ssrc = 1; ssrc <= MaxTrackedSsrcs; ssrc++)
            session.InjectInboundDatagramForTest(Packet(ssrc, seq: 1));

        // Touch SSRC 1 again so it is no longer the least-recently-active — SSRC 2 now is.
        session.InjectInboundDatagramForTest(Packet(1, seq: 2));

        // One more distinct SSRC forces a single eviction.
        session.InjectInboundDatagramForTest(Packet(1000, seq: 1));

        Assert.Equal(MaxTrackedSsrcs, session.TrackedSsrcCount);
        Assert.True(session.IsSsrcTracked(1), "recently-touched SSRC must survive");
        Assert.False(session.IsSsrcTracked(2), "least-recently-active SSRC must be evicted");
        Assert.True(session.IsSsrcTracked(1000), "the new SSRC must be tracked");
    }

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
