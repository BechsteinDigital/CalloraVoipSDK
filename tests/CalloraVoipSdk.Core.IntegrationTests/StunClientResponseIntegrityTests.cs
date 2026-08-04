using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Client;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Server;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #156 STUN P1-1 (Auth-Response-Integrität). A STUN client that matches responses only by transaction
/// id accepts a spoofed or corrupted datagram that guesses that id. These tests pin the hardened
/// response-admission predicate (class/method/source/FINGERPRINT/MESSAGE-INTEGRITY, RFC 5389
/// §7.3.3 / §10.1.2 / §15.5) and prove the first-party server now returns MESSAGE-INTEGRITY in
/// authenticated Binding Success responses so the client can verify them end to end.
/// </summary>
public sealed class StunClientResponseIntegrityTests
{
    private static readonly byte[] TxId =
        [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C];

    private static readonly IPEndPoint Server = new(IPAddress.Loopback, 3478);

    private static StunClient NewClient(StunMessageCodec codec)
        => new(codec, NullLogger<StunClient>.Instance);

    // ── Predicate: positive control ────────────────────────────────────────────

    [Fact]
    public void Accepts_a_success_response_with_matching_id_source_fingerprint_and_integrity()
    {
        var codec = new StunMessageCodec();
        var key = StunKeyDerivation.ShortTermKey("s3cret");
        var raw = codec.EncodeWithIntegrity(SuccessFor(TxId), key, addFingerprint: true);
        var msg = codec.Decode(raw)!;

        Assert.True(NewClient(codec).IsAcceptableResponse(msg, raw, TxId, key, Server, Server));
    }

    [Fact]
    public void Accepts_when_source_is_unknown_to_the_caller()
    {
        // Connected UDP / TCP streams are OS-filtered, so the caller passes a null source.
        var codec = new StunMessageCodec();
        var raw = codec.Encode(SuccessFor(TxId));
        var msg = codec.Decode(raw)!;

        Assert.True(NewClient(codec).IsAcceptableResponse(msg, raw, TxId, null, Server, actualSource: null));
    }

    [Fact]
    public void Accepts_an_ipv4_mapped_source_for_an_ipv4_server()
    {
        var codec = new StunMessageCodec();
        var raw = codec.Encode(SuccessFor(TxId));
        var msg = codec.Decode(raw)!;
        var mappedSource = new IPEndPoint(IPAddress.Loopback.MapToIPv6(), Server.Port);

        Assert.True(NewClient(codec).IsAcceptableResponse(msg, raw, TxId, null, Server, mappedSource));
    }

    // ── Predicate: rejections ──────────────────────────────────────────────────

    [Fact]
    public void Rejects_a_transaction_id_mismatch()
    {
        var codec = new StunMessageCodec();
        var other = (byte[])TxId.Clone();
        other[0] ^= 0xFF;
        var raw = codec.Encode(SuccessFor(other));
        var msg = codec.Decode(raw)!;

        Assert.False(NewClient(codec).IsAcceptableResponse(msg, raw, TxId, null, Server, Server));
    }

    [Fact]
    public void Rejects_a_non_response_class_with_a_matching_id()
    {
        var codec = new StunMessageCodec();
        var reflected = new StunMessage
        {
            MessageClass = StunMessageClass.Request,
            MessageMethod = StunMessageMethod.Binding,
            TransactionId = TxId,
            Attributes = [],
        };

        Assert.False(NewClient(codec).IsAcceptableResponse(reflected, [], TxId, null, Server, Server));
    }

    [Fact]
    public void Rejects_an_unexpected_method()
    {
        var codec = new StunMessageCodec();
        var wrongMethod = new StunMessage
        {
            MessageClass = StunMessageClass.SuccessResponse,
            MessageMethod = (StunMessageMethod)0x0002,
            TransactionId = TxId,
            Attributes = [],
        };

        Assert.False(NewClient(codec).IsAcceptableResponse(wrongMethod, [], TxId, null, Server, Server));
    }

    [Fact]
    public void Rejects_a_response_from_an_unexpected_source()
    {
        var codec = new StunMessageCodec();
        var raw = codec.Encode(SuccessFor(TxId));
        var msg = codec.Decode(raw)!;
        var attacker = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 3478);

        Assert.False(NewClient(codec).IsAcceptableResponse(msg, raw, TxId, null, Server, attacker));
    }

    [Fact]
    public void Rejects_a_present_but_invalid_fingerprint()
    {
        var codec = new StunMessageCodec();
        var raw = codec.EncodeWithIntegrity(SuccessFor(TxId), StunKeyDerivation.ShortTermKey("k"), addFingerprint: true);
        var msg = codec.Decode(raw)!; // decoded from the intact bytes → carries a FINGERPRINT attribute
        raw[StunWireConstants.HeaderSize + 5] ^= 0xFF; // corrupt an attribute byte so the CRC no longer matches

        Assert.Contains(msg.Attributes, a => a.AttributeType == StunAttributeType.Fingerprint);
        Assert.False(NewClient(codec).IsAcceptableResponse(msg, raw, TxId, null, Server, Server));
    }

    [Fact]
    public void Rejects_a_success_without_message_integrity_when_a_key_was_sent()
    {
        var codec = new StunMessageCodec();
        var raw = codec.Encode(SuccessFor(TxId)); // no MESSAGE-INTEGRITY
        var msg = codec.Decode(raw)!;

        Assert.False(NewClient(codec)
            .IsAcceptableResponse(msg, raw, TxId, StunKeyDerivation.ShortTermKey("s3cret"), Server, Server));
    }

    [Fact]
    public void Rejects_a_success_whose_message_integrity_uses_the_wrong_key()
    {
        var codec = new StunMessageCodec();
        var raw = codec.EncodeWithIntegrity(SuccessFor(TxId), StunKeyDerivation.ShortTermKey("theirs"), addFingerprint: false);
        var msg = codec.Decode(raw)!;

        Assert.False(NewClient(codec)
            .IsAcceptableResponse(msg, raw, TxId, StunKeyDerivation.ShortTermKey("ours"), Server, Server));
    }

    [Fact]
    public void Passes_an_error_response_through_without_requiring_integrity()
    {
        // 401/438 challenges are pre-auth and carry no MESSAGE-INTEGRITY the client can verify;
        // they must reach ProcessResponse so the long-term flow can read the challenge.
        var codec = new StunMessageCodec();
        var challenge = new StunMessage
        {
            MessageClass = StunMessageClass.ErrorResponse,
            MessageMethod = StunMessageMethod.Binding,
            TransactionId = TxId,
            Attributes = [new ErrorCodeAttribute { Code = 401, Reason = "Unauthorized" }],
        };
        var raw = codec.Encode(challenge);
        var msg = codec.Decode(raw)!;

        Assert.True(NewClient(codec)
            .IsAcceptableResponse(msg, raw, TxId, StunKeyDerivation.ShortTermKey("s3cret"), Server, Server));
    }

    // ── Server: authenticated success carries MESSAGE-INTEGRITY ────────────────

    [Fact]
    public void Server_protects_an_authenticated_success_response_with_the_credential_key()
    {
        var codec = new StunMessageCodec();
        var credentials = new StunCredentials { Username = "alice", Password = "s3cret" }; // short-term
        var handler = new StunBindingRequestHandler(codec, credentials, NullLogger<StunBindingRequestHandler>.Instance);

        var key = credentials.DeriveHmacKey();
        var request = new StunMessage
        {
            MessageClass = StunMessageClass.Request,
            MessageMethod = StunMessageMethod.Binding,
            TransactionId = TxId,
            Attributes = [new UsernameAttribute { Value = "alice" }],
        };
        var rawRequest = codec.EncodeWithIntegrity(request, key, addFingerprint: false);
        var decoded = codec.Decode(rawRequest)!;

        var result = handler.Handle(decoded, rawRequest, new IPEndPoint(IPAddress.Loopback, 40000));

        Assert.NotNull(result);
        Assert.Equal(StunMessageClass.SuccessResponse, result!.Response.MessageClass);
        Assert.NotNull(result.ResponseIntegrityKey);
        Assert.Equal(key, result.ResponseIntegrityKey);

        // Wire-level proof: encoding the response with the threaded key (as StunServer does)
        // yields bytes whose MESSAGE-INTEGRITY verifies against the credential key.
        var wire = codec.EncodeWithIntegrity(result.Response, result.ResponseIntegrityKey!);
        Assert.True(codec.VerifyIntegrity(wire, key));
    }

    // ── End to end: credentialed query round-trips over real UDP ───────────────

    [Fact]
    public async Task Authenticated_binding_query_round_trips_against_the_first_party_server()
    {
        var codec = new StunMessageCodec();
        var credentials = new StunCredentials { Username = "alice", Password = "s3cret" };

        await using var server = new StunServer(
            new IPEndPoint(IPAddress.Loopback, 0),
            codec,
            responseIntegrityKey: null,
            NullLogger<StunServer>.Instance);
        server.Start(new StunBindingRequestHandler(codec, credentials, NullLogger<StunBindingRequestHandler>.Instance));

        var client = NewClient(codec);
        var result = await client
            .QueryBindingAsync(server.LocalEndPoint, credentials: credentials)
            .WaitAsync(TimeSpan.FromSeconds(5));

        // Success is only returned when the server included MESSAGE-INTEGRITY and the client verified it.
        Assert.NotNull(result.MappedEndPoint);
    }

    private static StunMessage SuccessFor(byte[] transactionId)
        => StunMessage.CreateBindingResponse(
            transactionId,
            [new XorMappedAddressAttribute { EndPoint = new IPEndPoint(IPAddress.Loopback, 55555) }]);
}
