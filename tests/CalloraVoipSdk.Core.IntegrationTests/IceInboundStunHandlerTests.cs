using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Application.Media.Ice;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// End-to-end verification of inbound ICE on the shared media socket (RFC 8445 §7.3): a signed
/// STUN connectivity check sent to a real <see cref="RtpSession"/> is demuxed to the
/// <see cref="IceInboundStunHandler"/>, which authenticates it, resolves role conflicts, and sends
/// the Success / 487 response back on the same socket — with USE-CANDIDATE surfacing nomination.
/// </summary>
public sealed class IceInboundStunHandlerTests
{
    private const string LocalUfrag = "localU";
    private const string PeerUfrag = "peerU";
    private const string LocalPassword = "localPwd";

    private static readonly StunMessageCodec Codec = new();

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

    private static IceInboundStunHandler NewHandler(
        RtpSession session, IceRole role, ulong tieBreaker)
        => new(
            new IceInboundCheckProcessor(new IceInboundBindingResponder(Codec)),
            session.SendRawAsync,
            LocalUfrag,
            LocalPassword,
            tieBreaker,
            role,
            NullLoggerFactory.Instance);

    private static byte[] BuildCheck(bool peerControlling, ulong peerTieBreaker, bool useCandidate)
    {
        var attributes = new List<StunAttribute>
        {
            new UsernameAttribute { Value = LocalUfrag + ":" + PeerUfrag },
            new PriorityAttribute { Value = 100u },
            peerControlling
                ? new IceControllingAttribute { TieBreaker = peerTieBreaker }
                : new IceControlledAttribute { TieBreaker = peerTieBreaker },
        };
        if (useCandidate)
            attributes.Add(new UseCandidateAttribute());

        var request = new StunMessage
        {
            MessageClass = StunMessageClass.Request,
            MessageMethod = StunMessageMethod.Binding,
            TransactionId = StunMessage.CreateBindingRequest().TransactionId,
            Attributes = attributes,
        };
        return Codec.EncodeWithIntegrity(request, StunKeyDerivation.ShortTermKey(LocalPassword), addFingerprint: true);
    }

    [Fact]
    public async Task Inbound_check_receives_verifiable_success_response_on_media_socket()
    {
        var sessionPort = FreeUdpPort();
        var peerPort = FreeUdpPort();

        await using var session = new RtpSession(
            Options(sessionPort, peerPort), new RtpPacketCodec(), NullLogger<RtpSession>.Instance);
        var handler = NewHandler(session, IceRole.Controlled, tieBreaker: 1);
        session.StunPacketReceived += handler.OnStunPacketReceived;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await session.StartAsync(cts.Token);

        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));
        var check = BuildCheck(peerControlling: true, peerTieBreaker: 2, useCandidate: false);
        await peer.SendAsync(check, new IPEndPoint(IPAddress.Loopback, sessionPort), cts.Token);

        var reply = await peer.ReceiveAsync(cts.Token);

        var response = Codec.Decode(reply.Buffer);
        Assert.NotNull(response);
        Assert.Equal(StunMessageClass.SuccessResponse, response!.MessageClass);
        var mapped = response.Attributes.OfType<XorMappedAddressAttribute>().Single().EndPoint;
        Assert.Equal(peerPort, mapped.Port);
        Assert.True(Codec.VerifyIntegrity(reply.Buffer, StunKeyDerivation.ShortTermKey(LocalPassword)));
        Assert.True(Codec.VerifyFingerprint(reply.Buffer));
        Assert.Equal(IceRole.Controlled, handler.Role);
    }

    [Fact]
    public async Task Role_conflict_won_returns_487_and_keeps_controlling()
    {
        var sessionPort = FreeUdpPort();
        var peerPort = FreeUdpPort();

        await using var session = new RtpSession(
            Options(sessionPort, peerPort), new RtpPacketCodec(), NullLogger<RtpSession>.Instance);
        var handler = NewHandler(session, IceRole.Controlling, tieBreaker: 100);
        session.StunPacketReceived += handler.OnStunPacketReceived;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await session.StartAsync(cts.Token);

        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));
        // Peer also claims controlling but with a smaller tie-breaker → we win, reject with 487.
        var check = BuildCheck(peerControlling: true, peerTieBreaker: 50, useCandidate: false);
        await peer.SendAsync(check, new IPEndPoint(IPAddress.Loopback, sessionPort), cts.Token);

        var reply = await peer.ReceiveAsync(cts.Token);

        var response = Codec.Decode(reply.Buffer);
        Assert.Equal(StunMessageClass.ErrorResponse, response!.MessageClass);
        Assert.Equal(487, response.Attributes.OfType<ErrorCodeAttribute>().Single().Code);
        Assert.Equal(IceRole.Controlling, handler.Role);
    }

    [Fact]
    public async Task Controlled_agent_use_candidate_raises_nomination_and_answers_success()
    {
        var sessionPort = FreeUdpPort();
        var peerPort = FreeUdpPort();

        await using var session = new RtpSession(
            Options(sessionPort, peerPort), new RtpPacketCodec(), NullLogger<RtpSession>.Instance);
        var handler = NewHandler(session, IceRole.Controlled, tieBreaker: 1);

        var nominated = 0;
        handler.PairNominated += () => Interlocked.Increment(ref nominated);
        session.StunPacketReceived += handler.OnStunPacketReceived;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await session.StartAsync(cts.Token);

        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));
        var check = BuildCheck(peerControlling: true, peerTieBreaker: 2, useCandidate: true);
        await peer.SendAsync(check, new IPEndPoint(IPAddress.Loopback, sessionPort), cts.Token);

        var reply = await peer.ReceiveAsync(cts.Token);

        var response = Codec.Decode(reply.Buffer);
        Assert.Equal(StunMessageClass.SuccessResponse, response!.MessageClass);
        Assert.Equal(1, Volatile.Read(ref nominated));
    }
}
