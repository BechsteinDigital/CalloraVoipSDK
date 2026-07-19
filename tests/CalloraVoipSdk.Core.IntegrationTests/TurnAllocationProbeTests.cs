using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Behaviour of <see cref="TurnAllocationProbe"/>: a media-socket-bound one-shot TURN allocation used during
/// ICE candidate gathering. Runs the real auth handshake against a fake TURN server over a real socket and
/// returns the allocation, or null when the allocation is refused.
/// </summary>
public sealed class TurnAllocationProbeTests
{
    private static readonly IPEndPoint Relayed = new(IPAddress.Parse("198.51.100.9"), 49152);

    [Fact]
    public async Task TryAllocateAsync_returns_the_allocation_after_the_auth_handshake()
    {
        var codec = new StunMessageCodec();
        using var fakeServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var serverEndPoint = (IPEndPoint)fakeServer.Client.LocalEndPoint!;
        using var serverCts = new CancellationTokenSource();
        var serverLoop = RunFakeTurnServerAsync(fakeServer, codec, Relayed, succeed: true, serverCts.Token);

        using var media = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var probe = new TurnAllocationProbe(codec, NullLoggerFactory.Instance);
        var credentials = new StunCredentials { Username = "user", Password = "pass", Realm = "bootstrap" };

        var result = await probe
            .TryAllocateAsync(media.Client, serverEndPoint, credentials, lifetimeSeconds: 600, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(result);
        Assert.Equal(Relayed, result!.RelayedEndPoint);
        Assert.Equal(600u, result.LifetimeSeconds);
        Assert.Equal("callora", result.EffectiveCredentials?.Realm);
        Assert.Equal("nonce-1", result.EffectiveCredentials?.Nonce);

        serverCts.Cancel();
        await serverLoop;
    }

    [Fact]
    public async Task TryAllocateAsync_returns_null_when_the_allocation_is_refused()
    {
        var codec = new StunMessageCodec();
        using var fakeServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var serverEndPoint = (IPEndPoint)fakeServer.Client.LocalEndPoint!;
        using var serverCts = new CancellationTokenSource();
        var serverLoop = RunFakeTurnServerAsync(fakeServer, codec, Relayed, succeed: false, serverCts.Token);

        using var media = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var probe = new TurnAllocationProbe(codec, NullLoggerFactory.Instance);
        var credentials = new StunCredentials { Username = "user", Password = "pass", Realm = "bootstrap" };

        var result = await probe
            .TryAllocateAsync(media.Client, serverEndPoint, credentials, lifetimeSeconds: 600, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(result); // a refused allocation surfaces as no relay candidate, not an exception

        serverCts.Cancel();
        await serverLoop;
    }

    private static async Task RunFakeTurnServerAsync(
        UdpClient server, IStunMessageCodec codec, IPEndPoint relayed, bool succeed, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await server.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            var request = codec.Decode(received.Buffer);
            if (request is null || (TurnMessageMethod)(ushort)request.MessageMethod != TurnMessageMethod.Allocate)
                continue;

            byte[] response;
            if (!succeed)
                response = Error(codec, request, 400, "Bad Request");
            else if (!HasUsername(request))
                response = Error(codec, request, 401, "Unauthorized",
                    new RealmAttribute { Value = "callora" }, new NonceAttribute { Value = "nonce-1" });
            else
                response = AllocateSuccess(codec, request, relayed, 600);

            await server.SendAsync(response, response.Length, (IPEndPoint)received.RemoteEndPoint);
        }
    }

    private static bool HasUsername(StunMessage message) => message.Attributes.OfType<UsernameAttribute>().Any();

    private static byte[] Error(IStunMessageCodec codec, StunMessage req, int code, string reason, params StunAttribute[] extra)
    {
        var attributes = new List<StunAttribute> { new ErrorCodeAttribute { Code = code, Reason = reason } };
        attributes.AddRange(extra);
        return codec.Encode(new StunMessage
        {
            MessageClass = StunMessageClass.ErrorResponse,
            MessageMethod = req.MessageMethod,
            TransactionId = req.TransactionId,
            Attributes = attributes,
        });
    }

    private static byte[] AllocateSuccess(IStunMessageCodec codec, StunMessage req, IPEndPoint relayed, uint lifetimeSeconds)
        => codec.Encode(new StunMessage
        {
            MessageClass = StunMessageClass.SuccessResponse,
            MessageMethod = req.MessageMethod,
            TransactionId = req.TransactionId,
            Attributes =
            [
                TurnAttributeMapper.Encode(new TurnXorRelayedAddressAttribute { EndPoint = relayed }, req.TransactionId),
                TurnAttributeMapper.Encode(new TurnLifetimeAttribute { Seconds = lifetimeSeconds }),
            ],
        });
}
