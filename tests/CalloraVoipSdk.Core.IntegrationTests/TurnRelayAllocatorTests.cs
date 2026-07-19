using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Behaviour of <see cref="TurnRelayAllocator"/>: the Allocate → CreatePermission → ChannelBind sequence
/// over the shared socket that yields a bound <see cref="TurnRelayChannel"/>. A stateful in-memory fake
/// relay (routing responses by method) stands in for the bundle socket + TURN server, so these tests
/// exercise sequencing, credential threading and result construction without a socket.
/// </summary>
public sealed class TurnRelayAllocatorTests
{
    private static readonly TimeSpan FastRto = TimeSpan.FromMilliseconds(40);
    private static readonly IPEndPoint RelayServer = new(IPAddress.Parse("192.0.2.1"), 3478);
    private static readonly IPEndPoint Peer = new(IPAddress.Parse("203.0.113.7"), 50000);
    private static readonly IPEndPoint Relayed = new(IPAddress.Parse("198.51.100.9"), 49152);

    [Fact]
    public async Task EstablishAsync_runs_allocate_permission_channelbind_in_order_and_returns_the_bound_channel()
    {
        var requests = new List<StunMessage>();
        var allocator = NewAllocator(HappyPathRelay(requests));

        var credentials = new StunCredentials { Username = "user", Password = "pass", Realm = "bootstrap" };
        var result = await allocator.EstablishAsync(RelayServer, Peer, 0x4001, credentials, lifetimeSeconds: 600, CancellationToken.None);

        // Sequence: unauthenticated Allocate probe, authenticated Allocate, then Permission, then ChannelBind.
        Assert.Equal(
            new[] { TurnMessageMethod.Allocate, TurnMessageMethod.Allocate, TurnMessageMethod.CreatePermission, TurnMessageMethod.ChannelBind },
            requests.Select(r => (TurnMessageMethod)(ushort)r.MessageMethod).ToArray());

        // Only the first Allocate is an unauthenticated probe; the rest carry the threaded server realm/nonce.
        Assert.False(HasUsername(requests[0]));
        Assert.True(HasUsername(requests[1]));
        Assert.Equal("callora.example", RealmOf(requests[2]));
        Assert.Equal("callora.example", RealmOf(requests[3]));
        Assert.Equal("nonce-1", NonceOf(requests[3]));

        var channel = Assert.IsType<TurnRelayChannel>(result.Channel);
        Assert.Equal(RelayServer, channel.RelayServer);
        Assert.Equal((ushort)0x4001, channel.ChannelNumber);
        Assert.Equal(Relayed, result.RelayedEndPoint);
        Assert.Equal(600u, result.LifetimeSeconds);
        Assert.Equal("callora.example", result.EffectiveCredentials?.Realm);
        Assert.Equal("nonce-1", result.EffectiveCredentials?.Nonce);
    }

    [Fact]
    public async Task EstablishAsync_against_an_open_server_establishes_without_authentication()
    {
        var requests = new List<StunMessage>();
        // An open server never issues a 401 challenge.
        var allocator = NewAllocator((codec, transactor) => bytes =>
        {
            var request = codec.Decode(bytes.ToArray())!;
            requests.Add(request);
            var method = (TurnMessageMethod)(ushort)request.MessageMethod;
            transactor.OnControlDatagram(method == TurnMessageMethod.Allocate
                ? AllocateSuccess(codec, request, Relayed, 300)
                : EmptySuccess(codec, request));
        });

        var result = await allocator.EstablishAsync(RelayServer, Peer, 0x4002, credentials: null, lifetimeSeconds: null, CancellationToken.None);

        // No probe: null credentials -> a single unauthenticated request per step.
        Assert.Equal(
            new[] { TurnMessageMethod.Allocate, TurnMessageMethod.CreatePermission, TurnMessageMethod.ChannelBind },
            requests.Select(r => (TurnMessageMethod)(ushort)r.MessageMethod).ToArray());
        Assert.All(requests, r => Assert.False(HasUsername(r)));
        Assert.Equal((ushort)0x4002, Assert.IsType<TurnRelayChannel>(result.Channel).ChannelNumber);
        Assert.Null(result.EffectiveCredentials);
    }

    [Fact]
    public async Task EstablishAsync_propagates_a_channelbind_error()
    {
        var requests = new List<StunMessage>();
        // Allocate + Permission succeed; ChannelBind is rejected.
        var allocator = NewAllocator((codec, transactor) => bytes =>
        {
            var request = codec.Decode(bytes.ToArray())!;
            requests.Add(request);
            var method = (TurnMessageMethod)(ushort)request.MessageMethod;
            transactor.OnControlDatagram(method switch
            {
                TurnMessageMethod.Allocate => AllocateSuccess(codec, request, Relayed, 600),
                TurnMessageMethod.CreatePermission => EmptySuccess(codec, request),
                _ => Error(codec, request, 486, "Allocation Quota Reached")
            });
        });

        await Assert.ThrowsAsync<TurnException>(
            () => allocator.EstablishAsync(RelayServer, Peer, 0x4001, credentials: null, lifetimeSeconds: null, CancellationToken.None));

        Assert.Equal(TurnMessageMethod.ChannelBind, (TurnMessageMethod)(ushort)requests[^1].MessageMethod);
    }

    [Fact]
    public async Task EstablishAsync_rejects_a_channel_number_outside_the_turn_range()
    {
        var allocator = NewAllocator((_, _) => _ => { /* guard fails before any I/O */ });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => allocator.EstablishAsync(RelayServer, Peer, 0x3FFF, credentials: null, lifetimeSeconds: null, CancellationToken.None));
    }

    private static Func<StunMessageCodec, TurnControlTransactor, Action<ReadOnlyMemory<byte>>> HappyPathRelay(List<StunMessage> requests)
        => (codec, transactor) => bytes =>
        {
            var request = codec.Decode(bytes.ToArray())!;
            requests.Add(request);
            var method = (TurnMessageMethod)(ushort)request.MessageMethod;
            transactor.OnControlDatagram(method switch
            {
                TurnMessageMethod.Allocate when !HasUsername(request) => Challenge401(codec, request, "callora.example", "nonce-1"),
                TurnMessageMethod.Allocate => AllocateSuccess(codec, request, Relayed, 600),
                _ => EmptySuccess(codec, request)
            });
        };

    private static TurnRelayAllocator NewAllocator(Func<StunMessageCodec, TurnControlTransactor, Action<ReadOnlyMemory<byte>>> relayFactory)
    {
        var codec = new StunMessageCodec();
        var engine = new TurnTransactionEngine(codec);
        TurnControlTransactor transactor = null!;
        Action<ReadOnlyMemory<byte>> relay = null!;
        Task Send(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            relay(bytes);
            return Task.CompletedTask;
        }
        transactor = new TurnControlTransactor(codec, Send, NullLogger<TurnControlTransactor>.Instance, FastRto, maxAttempts: 3);
        relay = relayFactory(codec, transactor);
        return new TurnRelayAllocator(new TurnRelayControlClient(engine, transactor));
    }

    private static bool HasUsername(StunMessage message) => message.Attributes.OfType<UsernameAttribute>().Any();
    private static string? RealmOf(StunMessage message) => message.Attributes.OfType<RealmAttribute>().FirstOrDefault()?.Value;
    private static string? NonceOf(StunMessage message) => message.Attributes.OfType<NonceAttribute>().FirstOrDefault()?.Value;

    private static byte[] Challenge401(IStunMessageCodec codec, StunMessage req, string realm, string nonce)
        => Error(codec, req, 401, "Unauthorized",
            new RealmAttribute { Value = realm }, new NonceAttribute { Value = nonce });

    private static byte[] Error(IStunMessageCodec codec, StunMessage req, int code, string reason, params StunAttribute[] extra)
    {
        var attributes = new List<StunAttribute> { new ErrorCodeAttribute { Code = code, Reason = reason } };
        attributes.AddRange(extra);
        return codec.Encode(new StunMessage
        {
            MessageClass = StunMessageClass.ErrorResponse,
            MessageMethod = req.MessageMethod,
            TransactionId = req.TransactionId,
            Attributes = attributes
        });
    }

    private static byte[] EmptySuccess(IStunMessageCodec codec, StunMessage req)
        => codec.Encode(new StunMessage
        {
            MessageClass = StunMessageClass.SuccessResponse,
            MessageMethod = req.MessageMethod,
            TransactionId = req.TransactionId,
            Attributes = Array.Empty<StunAttribute>()
        });

    private static byte[] AllocateSuccess(IStunMessageCodec codec, StunMessage req, IPEndPoint relayed, uint lifetimeSeconds)
        => codec.Encode(new StunMessage
        {
            MessageClass = StunMessageClass.SuccessResponse,
            MessageMethod = req.MessageMethod,
            TransactionId = req.TransactionId,
            Attributes =
            [
                TurnAttributeMapper.Encode(new TurnXorRelayedAddressAttribute { EndPoint = relayed }, req.TransactionId),
                TurnAttributeMapper.Encode(new TurnLifetimeAttribute { Seconds = lifetimeSeconds })
            ]
        });
}
