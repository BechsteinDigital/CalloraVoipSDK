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
/// Behaviour of <see cref="TurnRelayControlClient"/>: authenticated TURN control operations over the
/// shared socket, composing <see cref="TurnTransactionEngine"/> (message build + long-term challenge flow)
/// with <see cref="TurnControlTransactor"/> (single round-trip). A stateful in-memory fake relay stands in
/// for the bundle socket + TURN server, so these tests exercise the real 401/438 auth orchestration and
/// MESSAGE-INTEGRITY-authenticated retries without a socket.
/// </summary>
public sealed class TurnRelayControlClientTests
{
    private static readonly TimeSpan FastRto = TimeSpan.FromMilliseconds(40);
    private static readonly IPEndPoint Peer = new(IPAddress.Parse("203.0.113.7"), 50000);
    private static readonly IPEndPoint Relayed = new(IPAddress.Parse("198.51.100.9"), 49152);

    [Fact]
    public async Task AllocateAsync_probes_unauthenticated_then_retries_authenticated_on_the_401_challenge()
    {
        var codec = new StunMessageCodec();
        var engine = new TurnTransactionEngine(codec);
        var requests = new List<StunMessage>();
        int sends = 0;
        TurnControlTransactor transactor = null!;
        Task Send(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            sends++;
            var request = codec.Decode(bytes.ToArray())!;
            requests.Add(request);
            transactor.OnControlDatagram(sends == 1
                ? Challenge401(codec, request, "callora.example", "nonce-1")
                : AllocateSuccess(codec, request, Relayed, lifetimeSeconds: 600));
            return Task.CompletedTask;
        }
        transactor = new TurnControlTransactor(codec, Send, NullLogger<TurnControlTransactor>.Instance, FastRto, maxAttempts: 3);
        var client = new TurnRelayControlClient(engine, transactor);

        var credentials = new StunCredentials { Username = "user", Password = "pass", Realm = "bootstrap" };
        var result = await client.AllocateAsync(credentials, lifetimeSeconds: 600, CancellationToken.None);

        Assert.Equal(2, sends);
        // First transmission is the unauthenticated probe — no credentials header.
        Assert.False(HasUsername(requests[0]));
        // Second carries the server's REALM/NONCE (authenticated with MESSAGE-INTEGRITY).
        Assert.True(HasUsername(requests[1]));
        Assert.Equal("callora.example", RealmOf(requests[1]));
        Assert.Equal("nonce-1", NonceOf(requests[1]));
        Assert.Equal(Relayed, result.RelayedEndPoint);
        Assert.Equal(600u, result.LifetimeSeconds);
        Assert.Equal("callora.example", result.EffectiveCredentials?.Realm);
        Assert.Equal("nonce-1", result.EffectiveCredentials?.Nonce);
    }

    [Fact]
    public async Task CreatePermissionAsync_sends_a_single_authenticated_request_with_primed_credentials()
    {
        var codec = new StunMessageCodec();
        var engine = new TurnTransactionEngine(codec);
        var requests = new List<StunMessage>();
        int sends = 0;
        TurnControlTransactor transactor = null!;
        Task Send(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            sends++;
            var request = codec.Decode(bytes.ToArray())!;
            requests.Add(request);
            transactor.OnControlDatagram(EmptySuccess(codec, request));
            return Task.CompletedTask;
        }
        transactor = new TurnControlTransactor(codec, Send, NullLogger<TurnControlTransactor>.Instance, FastRto, maxAttempts: 3);
        var client = new TurnRelayControlClient(engine, transactor);

        var primed = new StunCredentials { Username = "user", Password = "pass", Realm = "callora.example", Nonce = "nonce-1" };
        var effective = await client.CreatePermissionAsync(Peer, primed, CancellationToken.None);

        Assert.Equal(1, sends); // realm+nonce already known → no unauthenticated probe
        Assert.True(HasUsername(requests[0]));
        Assert.Equal("callora.example", RealmOf(requests[0]));
        Assert.NotNull(effective);
        Assert.Equal(TurnMessageMethod.CreatePermission, (TurnMessageMethod)(ushort)requests[0].MessageMethod);
    }

    [Fact]
    public async Task ChannelBindAsync_retries_with_the_fresh_nonce_on_a_438_stale_nonce()
    {
        var codec = new StunMessageCodec();
        var engine = new TurnTransactionEngine(codec);
        var requests = new List<StunMessage>();
        int sends = 0;
        TurnControlTransactor transactor = null!;
        Task Send(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            sends++;
            var request = codec.Decode(bytes.ToArray())!;
            requests.Add(request);
            transactor.OnControlDatagram(sends == 1
                ? StaleNonce438(codec, request, "nonce-2")
                : EmptySuccess(codec, request));
            return Task.CompletedTask;
        }
        transactor = new TurnControlTransactor(codec, Send, NullLogger<TurnControlTransactor>.Instance, FastRto, maxAttempts: 3);
        var client = new TurnRelayControlClient(engine, transactor);

        var primed = new StunCredentials { Username = "user", Password = "pass", Realm = "callora.example", Nonce = "nonce-1" };
        var effective = await client.ChannelBindAsync(Peer, 0x4001, primed, CancellationToken.None);

        Assert.Equal(2, sends);
        Assert.Equal("nonce-1", NonceOf(requests[0]));
        Assert.Equal("nonce-2", NonceOf(requests[1])); // retried with the server's fresh nonce
        Assert.Equal("nonce-2", effective?.Nonce);
    }

    [Fact]
    public async Task AllocateAsync_against_an_open_server_sends_a_single_unauthenticated_request()
    {
        var codec = new StunMessageCodec();
        var engine = new TurnTransactionEngine(codec);
        int sends = 0;
        TurnControlTransactor transactor = null!;
        Task Send(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            sends++;
            var request = codec.Decode(bytes.ToArray())!;
            transactor.OnControlDatagram(AllocateSuccess(codec, request, Relayed, lifetimeSeconds: 300));
            return Task.CompletedTask;
        }
        transactor = new TurnControlTransactor(codec, Send, NullLogger<TurnControlTransactor>.Instance, FastRto, maxAttempts: 3);
        var client = new TurnRelayControlClient(engine, transactor);

        var result = await client.AllocateAsync(credentials: null, lifetimeSeconds: null, CancellationToken.None);

        Assert.Equal(1, sends);
        Assert.Equal(Relayed, result.RelayedEndPoint);
        Assert.Equal(300u, result.LifetimeSeconds);
    }

    [Fact]
    public async Task AllocateAsync_raises_TurnException_when_the_success_response_lacks_a_relayed_address()
    {
        var codec = new StunMessageCodec();
        var engine = new TurnTransactionEngine(codec);
        TurnControlTransactor transactor = null!;
        Task Send(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            var request = codec.Decode(bytes.ToArray())!;
            transactor.OnControlDatagram(EmptySuccess(codec, request)); // success but no XOR-RELAYED-ADDRESS
            return Task.CompletedTask;
        }
        transactor = new TurnControlTransactor(codec, Send, NullLogger<TurnControlTransactor>.Instance, FastRto, maxAttempts: 3);
        var client = new TurnRelayControlClient(engine, transactor);

        await Assert.ThrowsAsync<TurnException>(
            () => client.AllocateAsync(credentials: null, lifetimeSeconds: null, CancellationToken.None));
    }

    [Fact]
    public async Task ChannelBindAsync_rejects_a_channel_number_outside_the_turn_range()
    {
        var codec = new StunMessageCodec();
        var engine = new TurnTransactionEngine(codec);
        Task Send(ReadOnlyMemory<byte> bytes, CancellationToken ct) => Task.CompletedTask;
        var transactor = new TurnControlTransactor(codec, Send, NullLogger<TurnControlTransactor>.Instance, FastRto);
        var client = new TurnRelayControlClient(engine, transactor);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.ChannelBindAsync(Peer, 0x3FFF, credentials: null, CancellationToken.None));
    }

    private static bool HasUsername(StunMessage message) => message.Attributes.OfType<UsernameAttribute>().Any();
    private static string? RealmOf(StunMessage message) => message.Attributes.OfType<RealmAttribute>().FirstOrDefault()?.Value;
    private static string? NonceOf(StunMessage message) => message.Attributes.OfType<NonceAttribute>().FirstOrDefault()?.Value;

    private static byte[] Challenge401(IStunMessageCodec codec, StunMessage req, string realm, string nonce)
        => Error(codec, req, 401, "Unauthorized",
            new RealmAttribute { Value = realm }, new NonceAttribute { Value = nonce });

    private static byte[] StaleNonce438(IStunMessageCodec codec, StunMessage req, string nonce)
        => Error(codec, req, 438, "Stale Nonce", new NonceAttribute { Value = nonce });

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
