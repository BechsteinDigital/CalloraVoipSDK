using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Verifies that a live <see cref="RtpCallMediaSession"/> activates the inbound ICE path
/// (Slice 3b-3 wiring): when ICE credentials are negotiated it answers STUN connectivity checks
/// arriving on the media socket (RFC 8445 §7.3), and the factory adds no handler for a non-ICE call.
/// </summary>
public sealed class RtpCallMediaSessionIceInboundTests
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

    private static byte[] BuildControlledCheck()
    {
        // Peer signals ICE-CONTROLLED, so against the session's controlling role there is no
        // role conflict — the check is accepted deterministically.
        var request = new StunMessage
        {
            MessageClass = StunMessageClass.Request,
            MessageMethod = StunMessageMethod.Binding,
            TransactionId = StunMessage.CreateBindingRequest().TransactionId,
            Attributes =
            [
                new UsernameAttribute { Value = LocalUfrag + ":" + PeerUfrag },
                new PriorityAttribute { Value = 100u },
                new IceControlledAttribute { TieBreaker = 42 },
            ],
        };
        return Codec.EncodeWithIntegrity(request, StunKeyDerivation.ShortTermKey(LocalPassword), addFingerprint: true);
    }

    [Fact]
    public void Factory_adds_handler_only_when_ice_credentials_are_present()
    {
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> send = (_, _, _) => ValueTask.CompletedTask;

        Assert.Null(IceInboundStunHandlerFactory.Create(null, null, send, NullLoggerFactory.Instance));
        Assert.Null(IceInboundStunHandlerFactory.Create(LocalUfrag, "   ", send, NullLoggerFactory.Instance));
        Assert.NotNull(IceInboundStunHandlerFactory.Create(LocalUfrag, LocalPassword, send, NullLoggerFactory.Instance));
    }

    [Fact]
    public async Task Ice_enabled_session_answers_inbound_check_on_the_media_socket()
    {
        var sessionPort = FreeUdpPort();
        var peerPort = FreeUdpPort();

        var parameters = new CallMediaParameters
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, sessionPort),
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, peerPort),
            PayloadType = 0,
            ClockRate = 8000,
            SamplesPerPacket = 160,
            IceEnabled = true,
            LocalIceUfrag = LocalUfrag,
            LocalIcePwd = LocalPassword,
        };

        await using var session = new RtpCallMediaSession(parameters, NullLoggerFactory.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await session.StartAsync(cts.Token);

        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));
        await peer.SendAsync(BuildControlledCheck(), new IPEndPoint(IPAddress.Loopback, sessionPort), cts.Token);

        var reply = await peer.ReceiveAsync(cts.Token);

        var response = Codec.Decode(reply.Buffer);
        Assert.NotNull(response);
        Assert.Equal(StunMessageClass.SuccessResponse, response!.MessageClass);
        Assert.Equal(peerPort, response.Attributes.OfType<XorMappedAddressAttribute>().Single().EndPoint.Port);
        Assert.True(Codec.VerifyIntegrity(reply.Buffer, StunKeyDerivation.ShortTermKey(LocalPassword)));
    }
}
