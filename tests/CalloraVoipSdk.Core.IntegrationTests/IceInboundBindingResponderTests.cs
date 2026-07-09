using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Verifies the inbound ICE connectivity-check wire layer (RFC 8445 §7.3): decoding the request
/// and its ICE attributes, verifying MESSAGE-INTEGRITY against the local ICE password, and building
/// the Success response (with XOR-MAPPED-ADDRESS) and the 487 Role Conflict error response — each
/// protected with MESSAGE-INTEGRITY and FINGERPRINT and correlated by transaction ID.
/// </summary>
public sealed class IceInboundBindingResponderTests
{
    private const string LocalPassword = "localIcePwd";
    private const string LocalUfrag = "ourUfrag";
    private const string PeerUfrag = "peerUfrag";

    private static readonly IPEndPoint Sender = new(IPAddress.Loopback, 50000);
    private static readonly byte[] TxId =
        [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

    private static readonly StunMessageCodec Codec = new();

    private static byte[] BuildInboundCheck(
        bool? peerControlling = true,
        ulong peerTieBreaker = 0xAABBCCDD11223344,
        bool useCandidate = false,
        uint priority = 987654u,
        string username = LocalUfrag + ":" + PeerUfrag,
        string signingPassword = LocalPassword)
    {
        var attributes = new List<StunAttribute>
        {
            new UsernameAttribute { Value = username },
            new PriorityAttribute { Value = priority },
        };

        if (peerControlling is true)
            attributes.Add(new IceControllingAttribute { TieBreaker = peerTieBreaker });
        else if (peerControlling is false)
            attributes.Add(new IceControlledAttribute { TieBreaker = peerTieBreaker });

        if (useCandidate)
            attributes.Add(new UseCandidateAttribute());

        var request = new StunMessage
        {
            MessageClass = StunMessageClass.Request,
            MessageMethod = StunMessageMethod.Binding,
            TransactionId = TxId,
            Attributes = attributes,
        };

        return Codec.EncodeWithIntegrity(
            request, StunKeyDerivation.ShortTermKey(signingPassword), addFingerprint: true);
    }

    [Fact]
    public void TryParse_extracts_username_role_priority_and_use_candidate()
    {
        var responder = new IceInboundBindingResponder(Codec);
        var raw = BuildInboundCheck(
            peerControlling: true, peerTieBreaker: 0x1122334455667788, useCandidate: true, priority: 4242u);

        var parsed = responder.TryParse(raw);

        Assert.NotNull(parsed);
        Assert.Equal(LocalUfrag + ":" + PeerUfrag, parsed!.Value.Username);
        Assert.True(parsed.Value.PeerControlling);
        Assert.Equal(0x1122334455667788ul, parsed.Value.PeerTieBreaker);
        Assert.True(parsed.Value.HasUseCandidate);
        Assert.Equal(4242u, parsed.Value.Priority);
    }

    [Fact]
    public void TryParse_reads_controlled_role_and_absent_use_candidate()
    {
        var responder = new IceInboundBindingResponder(Codec);
        var raw = BuildInboundCheck(peerControlling: false, peerTieBreaker: 7, useCandidate: false);

        var parsed = responder.TryParse(raw);

        Assert.NotNull(parsed);
        Assert.False(parsed!.Value.PeerControlling);
        Assert.Equal(7ul, parsed.Value.PeerTieBreaker);
        Assert.False(parsed.Value.HasUseCandidate);
    }

    [Fact]
    public void TryParse_returns_null_for_non_stun_bytes()
    {
        var responder = new IceInboundBindingResponder(Codec);
        // RTP-shaped bytes (version 2, PT 0), no STUN magic cookie.
        var rtpLike = new byte[] { 0x80, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        Assert.Null(responder.TryParse(rtpLike));
    }

    [Fact]
    public void TryParse_returns_null_for_binding_success_response()
    {
        var responder = new IceInboundBindingResponder(Codec);
        var response = StunMessage.CreateBindingResponse(
            TxId, [new XorMappedAddressAttribute { EndPoint = Sender }]);
        var raw = Codec.Encode(response);

        // A response to one of our own checks is not an inbound request — fall through.
        Assert.Null(responder.TryParse(raw));
    }

    [Fact]
    public void VerifyIntegrity_true_with_local_password()
    {
        var responder = new IceInboundBindingResponder(Codec);
        var raw = BuildInboundCheck(signingPassword: LocalPassword);

        Assert.True(responder.VerifyIntegrity(raw, LocalPassword));
    }

    [Fact]
    public void VerifyIntegrity_false_with_wrong_password()
    {
        var responder = new IceInboundBindingResponder(Codec);
        var raw = BuildInboundCheck(signingPassword: "someOtherPassword");

        Assert.False(responder.VerifyIntegrity(raw, LocalPassword));
    }

    [Fact]
    public void VerifyIntegrity_false_when_request_has_no_message_integrity()
    {
        var responder = new IceInboundBindingResponder(Codec);
        var request = new StunMessage
        {
            MessageClass = StunMessageClass.Request,
            MessageMethod = StunMessageMethod.Binding,
            TransactionId = TxId,
            Attributes = [new UsernameAttribute { Value = LocalUfrag + ":" + PeerUfrag }],
        };
        var raw = Codec.Encode(request); // no MESSAGE-INTEGRITY appended

        Assert.False(responder.VerifyIntegrity(raw, LocalPassword));
    }

    [Fact]
    public void BuildSuccessResponse_carries_mapped_address_and_verifies()
    {
        var responder = new IceInboundBindingResponder(Codec);
        var request = responder.TryParse(BuildInboundCheck())!.Value.Message;

        var raw = responder.BuildSuccessResponse(request, Sender, LocalPassword);

        var decoded = Codec.Decode(raw);
        Assert.NotNull(decoded);
        Assert.Equal(StunMessageClass.SuccessResponse, decoded!.MessageClass);
        Assert.Equal(StunMessageMethod.Binding, decoded.MessageMethod);
        Assert.Equal(TxId, decoded.TransactionId);
        Assert.Equal(Sender, decoded.Attributes.OfType<XorMappedAddressAttribute>().Single().EndPoint);
        Assert.True(Codec.VerifyIntegrity(raw, StunKeyDerivation.ShortTermKey(LocalPassword)));
        Assert.True(Codec.VerifyFingerprint(raw));
    }

    [Fact]
    public void BuildRoleConflictResponse_is_487_and_verifies()
    {
        var responder = new IceInboundBindingResponder(Codec);
        var request = responder.TryParse(BuildInboundCheck())!.Value.Message;

        var raw = responder.BuildRoleConflictResponse(request, LocalPassword);

        var decoded = Codec.Decode(raw);
        Assert.NotNull(decoded);
        Assert.Equal(StunMessageClass.ErrorResponse, decoded!.MessageClass);
        Assert.Equal(TxId, decoded.TransactionId);
        Assert.Equal(487, decoded.Attributes.OfType<ErrorCodeAttribute>().Single().Code);
        Assert.True(Codec.VerifyIntegrity(raw, StunKeyDerivation.ShortTermKey(LocalPassword)));
        Assert.True(Codec.VerifyFingerprint(raw));
    }
}
