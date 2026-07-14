using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RtpSession secondary-stream transport (WebRTC phase 3, groundwork for RFC 4588 RTX): a
/// configured payload type is sent/received with its own SRTP context and a separate replay
/// window, apart from the primary stream — proven over real UDP loopback.
/// </summary>
public sealed class RtpSessionSecondaryStreamTests
{
    private const byte PrimaryPt = 96;
    private const byte SecondaryPt = 98;
    private static readonly RtpPacketCodec RtpCodec = new();

    [Fact]
    public async Task Secondary_payload_type_is_dispatched_apart_from_the_primary_stream()
    {
        var localPort = FreeUdpPort();
        await using var session = CreateSession(localPort, FreeUdpPort(), configureSecondary: true);

        var primary = new TaskCompletionSource<RtpPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondary = new TaskCompletionSource<RtpPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PacketReceived += (_, p) => primary.TrySetResult(p);
        session.SecondaryPacketReceived += p => secondary.TrySetResult(p);
        await session.StartAsync();

        using var sender = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, localPort);
        await sender.SendAsync(Packet(PrimaryPt, seq: 1, ssrc: 0xA, payload: [0x10, 0x01]), target);
        await sender.SendAsync(Packet(SecondaryPt, seq: 5, ssrc: 0xB, payload: [0xDE, 0xAD]), target);

        var primaryPacket = await primary.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondaryPacket = await secondary.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PrimaryPt, primaryPacket.PayloadType);
        Assert.Equal(SecondaryPt, secondaryPacket.PayloadType);
        Assert.Equal(0xBu, secondaryPacket.Ssrc);
        Assert.Equal(new byte[] { 0xDE, 0xAD }, secondaryPacket.Payload.ToArray());
    }

    [Fact]
    public async Task Secondary_stream_round_trips_encrypted_with_its_own_srtp_context()
    {
        var localPort = FreeUdpPort();
        await using var session = CreateSession(localPort, FreeUdpPort(), configureSecondary: true);

        // The peer encrypts the secondary stream with the master key we un-protect with.
        var keys = SrtpKeyMaterial.ParseInline(InlineKey(7), SrtpCryptoSuite.AesCm128HmacSha1_80);
        session.InstallSecondarySecurityContexts(new SrtpContext(keys), new SrtpContext(keys));
        // A primary inbound context so RequireEncryptedMedia is satisfied for both paths.
        var primaryKeys = SrtpKeyMaterial.ParseInline(InlineKey(1), SrtpCryptoSuite.AesCm128HmacSha1_80);
        session.InstallSecurityContexts(
            new SrtpContext(primaryKeys), new SrtpContext(primaryKeys),
            new SrtcpContext(primaryKeys), new SrtcpContext(primaryKeys));

        var received = new TaskCompletionSource<RtpPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.SecondaryPacketReceived += p => received.TrySetResult(p);
        await session.StartAsync();

        using var peerContext = new SrtpContext(keys);
        var wire = peerContext.Protect(Packet(SecondaryPt, seq: 10, ssrc: 0xC, payload: [1, 2, 3, 4]));
        using var sender = new UdpClient();
        await sender.SendAsync(wire, new IPEndPoint(IPAddress.Loopback, localPort));

        var packet = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, packet.Payload.ToArray());
    }

    [Fact]
    public async Task Secondary_replay_does_not_advance_the_primary_replay_window()
    {
        // The core purpose of separate contexts (RFC 4588 §9): a replay on the secondary
        // stream must not affect the primary stream — proven by a primary packet still
        // being delivered at a sequence number the secondary stream also used.
        var localPort = FreeUdpPort();
        await using var session = CreateSession(localPort, FreeUdpPort(), configureSecondary: true);

        var primaryKeys = SrtpKeyMaterial.ParseInline(InlineKey(1), SrtpCryptoSuite.AesCm128HmacSha1_80);
        var secondaryKeys = SrtpKeyMaterial.ParseInline(InlineKey(2), SrtpCryptoSuite.AesCm128HmacSha1_80);
        session.InstallSecurityContexts(
            new SrtpContext(primaryKeys), new SrtpContext(primaryKeys),
            new SrtcpContext(primaryKeys), new SrtcpContext(primaryKeys));
        session.InstallSecondarySecurityContexts(new SrtpContext(secondaryKeys), new SrtpContext(secondaryKeys));

        var primaryDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PacketReceived += (_, _) => primaryDelivered.TrySetResult();
        await session.StartAsync();

        using var sender = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, localPort);

        // Send secondary seq 50 twice (a replay), then a primary packet at seq 50.
        using var peerSecondary = new SrtpContext(secondaryKeys);
        var secondaryWire = peerSecondary.Protect(Packet(SecondaryPt, seq: 50, ssrc: 0xB, payload: [1]));
        await sender.SendAsync(secondaryWire, target);
        await sender.SendAsync(secondaryWire, target); // replay — dropped by the secondary window

        using var peerPrimary = new SrtpContext(primaryKeys);
        var primaryWire = peerPrimary.Protect(Packet(PrimaryPt, seq: 50, ssrc: 0xA, payload: [2]));
        await sender.SendAsync(primaryWire, target);

        // The primary packet at seq 50 arrives — the secondary replay never touched its window.
        await primaryDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Secondary_packet_failing_authentication_is_dropped_without_killing_the_loop()
    {
        var localPort = FreeUdpPort();
        await using var session = CreateSession(localPort, FreeUdpPort(), configureSecondary: true);
        var keys = SrtpKeyMaterial.ParseInline(InlineKey(3), SrtpCryptoSuite.AesCm128HmacSha1_80);
        session.InstallSecondarySecurityContexts(new SrtpContext(keys), new SrtpContext(keys));

        var delivered = 0;
        session.SecondaryPacketReceived += _ => Interlocked.Increment(ref delivered);
        await session.StartAsync();

        using var sender = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, localPort);

        // A packet encrypted with the wrong key fails the auth tag → dropped.
        using var wrongContext = new SrtpContext(
            SrtpKeyMaterial.ParseInline(InlineKey(99), SrtpCryptoSuite.AesCm128HmacSha1_80));
        await sender.SendAsync(wrongContext.Protect(Packet(SecondaryPt, seq: 1, ssrc: 0xB, payload: [1])), target);

        // A correctly keyed packet still gets through — the loop survived the bad one.
        using var goodContext = new SrtpContext(keys);
        var good = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.SecondaryPacketReceived += _ => good.TrySetResult();
        await sender.SendAsync(goodContext.Protect(Packet(SecondaryPt, seq: 2, ssrc: 0xB, payload: [2])), target);

        await good.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref delivered));
    }

    [Fact]
    public async Task Secondary_send_reaches_the_peer_and_carries_its_own_ssrc()
    {
        var peerPort = FreeUdpPort();
        await using var session = CreateSession(FreeUdpPort(), peerPort, configureSecondary: true);
        await session.StartAsync();

        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));
        var rtx = new RtpPacket
        {
            PayloadType = SecondaryPt,
            SequenceNumber = 3,
            Timestamp = 9000,
            Ssrc = 0xF00D,
            Payload = new byte[] { 0x01, 0x02, 0xAA },
        };
        await session.SendSecondaryAsync(rtx);

        var datagram = (await peer.ReceiveAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token)).Buffer;
        var decoded = RtpCodec.Decode(datagram);
        Assert.Equal(SecondaryPt, decoded.PayloadType);
        Assert.Equal(0xF00Du, decoded.Ssrc);
    }

    [Fact]
    public async Task Secondary_send_is_suppressed_when_encrypted_media_required_but_unkeyed()
    {
        var peerPort = FreeUdpPort();
        await using var session = CreateSession(
            FreeUdpPort(), peerPort, configureSecondary: true, requireEncryptedMedia: true);
        await session.StartAsync();

        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));
        await session.SendSecondaryAsync(new RtpPacket
        {
            PayloadType = SecondaryPt, SequenceNumber = 1, Ssrc = 0x1, Payload = new byte[] { 9 },
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await peer.ReceiveAsync(timeout.Token));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static RtpSession CreateSession(
        int localPort, int remotePort, bool configureSecondary, bool requireEncryptedMedia = false)
    {
        var session = new RtpSession(
            new RtpSessionOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, remotePort),
                PayloadType = PrimaryPt,
                ClockRate = 90000,
                SamplesPerPacket = 3000,
                RequireEncryptedMedia = requireEncryptedMedia,
            },
            RtpCodec, NullLogger<RtpSession>.Instance);
        if (configureSecondary)
            session.ConfigureSecondaryStream(SecondaryPt);
        return session;
    }

    private static byte[] Packet(byte pt, ushort seq, uint ssrc, byte[] payload) => RtpCodec.Encode(new RtpPacket
    {
        PayloadType = pt,
        SequenceNumber = seq,
        Timestamp = (uint)(seq * 3000),
        Ssrc = ssrc,
        Payload = payload,
    });

    private static string InlineKey(byte seed)
    {
        var material = new byte[30];
        for (var i = 0; i < material.Length; i++)
            material[i] = (byte)(seed + i);
        return $"inline:{Convert.ToBase64String(material)}";
    }

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
