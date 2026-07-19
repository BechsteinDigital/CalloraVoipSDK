using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Behaviour of <see cref="TurnControlTransactor"/>: the shared-socket TURN request/response engine that
/// sends via an injected delegate and is fed responses through <see cref="TurnControlTransactor.OnControlDatagram"/>,
/// correlating by transaction id. A fake in-memory relay stands in for the bundle socket, so these
/// tests exercise correlation, RTO retransmission, challenge/error surfacing and cancellation without a
/// real socket.
/// </summary>
public sealed class TurnControlTransactorTests
{
    private static readonly TimeSpan FastRto = TimeSpan.FromMilliseconds(40);

    [Fact]
    public async Task RoundTrip_returns_the_transaction_matched_success_response()
    {
        var codec = new StunMessageCodec();
        TurnControlTransactor transactor = null!;
        Task Send(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            var request = codec.Decode(bytes.ToArray())!;
            transactor.OnControlDatagram(SuccessFor(codec, request));
            return Task.CompletedTask;
        }
        transactor = new TurnControlTransactor(codec, Send, NullLogger<TurnControlTransactor>.Instance, FastRto, maxAttempts: 3);

        var (req, bytes) = NewRequest(codec);
        var response = await transactor.RoundTripAsync(req, bytes, CancellationToken.None);

        Assert.Equal(StunMessageClass.SuccessResponse, response.MessageClass);
        Assert.Equal(req.MessageMethod, response.MessageMethod);
        Assert.Equal(req.TransactionId, response.TransactionId);
    }

    [Fact]
    public async Task RoundTrip_retransmits_then_succeeds_on_the_second_attempt()
    {
        var codec = new StunMessageCodec();
        int sends = 0;
        TurnControlTransactor transactor = null!;
        Task Send(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            sends++;
            if (sends >= 2)
            {
                var request = codec.Decode(bytes.ToArray())!;
                transactor.OnControlDatagram(SuccessFor(codec, request));
            }
            return Task.CompletedTask;
        }
        transactor = new TurnControlTransactor(codec, Send, NullLogger<TurnControlTransactor>.Instance, FastRto, maxAttempts: 5);

        var (req, bytes) = NewRequest(codec);
        var response = await transactor.RoundTripAsync(req, bytes, CancellationToken.None);

        Assert.Equal(StunMessageClass.SuccessResponse, response.MessageClass);
        Assert.Equal(2, sends);
    }

    [Fact]
    public async Task RoundTrip_ignores_a_response_with_a_different_transaction_id_and_times_out()
    {
        var codec = new StunMessageCodec();
        TurnControlTransactor transactor = null!;
        Task Send(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            var request = codec.Decode(bytes.ToArray())!;
            var foreignTxId = (byte[])request.TransactionId.Clone();
            foreignTxId[0] ^= 0xFF; // a different transaction than the one we registered
            var stray = new StunMessage
            {
                MessageClass = StunMessageClass.SuccessResponse,
                MessageMethod = request.MessageMethod,
                TransactionId = foreignTxId,
                Attributes = Array.Empty<StunAttribute>()
            };
            transactor.OnControlDatagram(codec.Encode(stray));
            return Task.CompletedTask;
        }
        transactor = new TurnControlTransactor(codec, Send, NullLogger<TurnControlTransactor>.Instance, FastRto, maxAttempts: 2);

        var (req, bytes) = NewRequest(codec);
        await Assert.ThrowsAsync<TurnException>(() => transactor.RoundTripAsync(req, bytes, CancellationToken.None));
    }

    [Fact]
    public async Task RoundTrip_surfaces_a_401_challenge_with_realm_and_nonce()
    {
        var codec = new StunMessageCodec();
        TurnControlTransactor transactor = null!;
        Task Send(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            var request = codec.Decode(bytes.ToArray())!;
            transactor.OnControlDatagram(ErrorFor(codec, request, 401, "Unauthorized",
                new RealmAttribute { Value = "callora.example" },
                new NonceAttribute { Value = "nonce-abc" }));
            return Task.CompletedTask;
        }
        transactor = new TurnControlTransactor(codec, Send, NullLogger<TurnControlTransactor>.Instance, FastRto, maxAttempts: 3);

        var (req, bytes) = NewRequest(codec);
        var challenge = await Assert.ThrowsAsync<TurnChallengeException>(
            () => transactor.RoundTripAsync(req, bytes, CancellationToken.None));

        Assert.Equal(401, challenge.ErrorCode);
        Assert.Equal("callora.example", challenge.Realm);
        Assert.Equal("nonce-abc", challenge.Nonce);
    }

    [Fact]
    public async Task RoundTrip_raises_a_plain_TurnException_on_a_non_challenge_error()
    {
        var codec = new StunMessageCodec();
        TurnControlTransactor transactor = null!;
        Task Send(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            var request = codec.Decode(bytes.ToArray())!;
            transactor.OnControlDatagram(ErrorFor(codec, request, 486, "Allocation Quota Reached"));
            return Task.CompletedTask;
        }
        transactor = new TurnControlTransactor(codec, Send, NullLogger<TurnControlTransactor>.Instance, FastRto, maxAttempts: 3);

        var (req, bytes) = NewRequest(codec);
        var ex = await Assert.ThrowsAsync<TurnException>(
            () => transactor.RoundTripAsync(req, bytes, CancellationToken.None));
        Assert.IsNotType<TurnChallengeException>(ex);
    }

    [Fact]
    public async Task RoundTrip_raises_TurnException_on_a_response_method_mismatch()
    {
        var codec = new StunMessageCodec();
        TurnControlTransactor transactor = null!;
        Task Send(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            var request = codec.Decode(bytes.ToArray())!;
            var wrongMethod = new StunMessage
            {
                MessageClass = StunMessageClass.SuccessResponse,
                MessageMethod = (StunMessageMethod)(ushort)TurnMessageMethod.ChannelBind, // request was Allocate
                TransactionId = request.TransactionId,
                Attributes = Array.Empty<StunAttribute>()
            };
            transactor.OnControlDatagram(codec.Encode(wrongMethod));
            return Task.CompletedTask;
        }
        transactor = new TurnControlTransactor(codec, Send, NullLogger<TurnControlTransactor>.Instance, FastRto, maxAttempts: 3);

        var (req, bytes) = NewRequest(codec, TurnMessageMethod.Allocate);
        await Assert.ThrowsAsync<TurnException>(() => transactor.RoundTripAsync(req, bytes, CancellationToken.None));
    }

    [Fact]
    public async Task RoundTrip_times_out_after_the_configured_attempts_when_no_response_arrives()
    {
        var codec = new StunMessageCodec();
        int sends = 0;
        Task Send(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            sends++;
            return Task.CompletedTask;
        }
        var transactor = new TurnControlTransactor(codec, Send, NullLogger<TurnControlTransactor>.Instance, FastRto, maxAttempts: 3);

        var (req, bytes) = NewRequest(codec);
        await Assert.ThrowsAsync<TurnException>(() => transactor.RoundTripAsync(req, bytes, CancellationToken.None));
        Assert.Equal(3, sends);
    }

    [Fact]
    public async Task RoundTrip_honours_cancellation_while_awaiting_a_response()
    {
        var codec = new StunMessageCodec();
        Task Send(ReadOnlyMemory<byte> bytes, CancellationToken ct) => Task.CompletedTask; // never respond
        var transactor = new TurnControlTransactor(
            codec, Send, NullLogger<TurnControlTransactor>.Instance,
            initialRto: TimeSpan.FromSeconds(30), maxAttempts: 7);

        using var cts = new CancellationTokenSource();
        var (req, bytes) = NewRequest(codec);
        var pending = transactor.RoundTripAsync(req, bytes, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public void OnControlDatagram_ignores_undecodable_and_unmatched_datagrams()
    {
        var codec = new StunMessageCodec();
        Task Send(ReadOnlyMemory<byte> bytes, CancellationToken ct) => Task.CompletedTask;
        var transactor = new TurnControlTransactor(codec, Send, NullLogger<TurnControlTransactor>.Instance, FastRto);

        // Garbage bytes (not a STUN message) and a well-formed response for an unknown transaction must
        // both be swallowed without throwing — this is the control path, not the media path.
        transactor.OnControlDatagram(new byte[] { 0x01, 0x02, 0x03 });

        var unknown = new StunMessage
        {
            MessageClass = StunMessageClass.SuccessResponse,
            MessageMethod = (StunMessageMethod)(ushort)TurnMessageMethod.Allocate,
            TransactionId = NewTransactionId(),
            Attributes = Array.Empty<StunAttribute>()
        };
        transactor.OnControlDatagram(codec.Encode(unknown));
    }

    [Fact]
    public async Task RoundTrip_wraps_a_transport_send_failure_as_a_TurnException()
    {
        var codec = new StunMessageCodec();
        Task Send(ReadOnlyMemory<byte> bytes, CancellationToken ct)
            => throw new InvalidOperationException("transport is down");
        var transactor = new TurnControlTransactor(codec, Send, NullLogger<TurnControlTransactor>.Instance, FastRto, maxAttempts: 3);

        var (req, bytes) = NewRequest(codec);
        var ex = await Assert.ThrowsAsync<TurnException>(
            () => transactor.RoundTripAsync(req, bytes, CancellationToken.None));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    private static (StunMessage Request, byte[] Bytes) NewRequest(
        IStunMessageCodec codec,
        TurnMessageMethod method = TurnMessageMethod.Allocate)
    {
        var request = new StunMessage
        {
            MessageClass = StunMessageClass.Request,
            MessageMethod = (StunMessageMethod)(ushort)method,
            TransactionId = NewTransactionId(),
            Attributes = Array.Empty<StunAttribute>()
        };
        return (request, codec.Encode(request));
    }

    private static byte[] SuccessFor(IStunMessageCodec codec, StunMessage request, params StunAttribute[] attributes)
        => codec.Encode(new StunMessage
        {
            MessageClass = StunMessageClass.SuccessResponse,
            MessageMethod = request.MessageMethod,
            TransactionId = request.TransactionId,
            Attributes = attributes
        });

    private static byte[] ErrorFor(
        IStunMessageCodec codec,
        StunMessage request,
        int code,
        string reason,
        params StunAttribute[] extra)
    {
        var attributes = new List<StunAttribute> { new ErrorCodeAttribute { Code = code, Reason = reason } };
        attributes.AddRange(extra);
        return codec.Encode(new StunMessage
        {
            MessageClass = StunMessageClass.ErrorResponse,
            MessageMethod = request.MessageMethod,
            TransactionId = request.TransactionId,
            Attributes = attributes
        });
    }

    private static byte[] NewTransactionId()
    {
        var transactionId = new byte[StunWireConstants.TransactionIdLength];
        RandomNumberGenerator.Fill(transactionId);
        return transactionId;
    }
}
