using System.Net;
using CalloraVoipSdk.Core.Application.Media.Ice;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Verifies the inbound ICE check processor (RFC 8445 §7.3) that ties decoding + authentication
/// (<c>IceInboundBindingResponder</c>) to the ICE decision (<c>IceInboundCheckEvaluator</c>):
/// non-STUN passthrough, discard on bad MESSAGE-INTEGRITY or wrong USERNAME, Success response for
/// accepted checks, 487 on a lost role conflict, role switch on a won-by-peer conflict, and
/// USE-CANDIDATE nomination by a controlled agent.
/// </summary>
public sealed class IceInboundCheckProcessorTests
{
    private const string LocalUfrag = "ourUfrag";
    private const string PeerUfrag = "peerUfrag";
    private const string LocalPassword = "localIcePwd";

    private static readonly IPEndPoint Sender = new(IPAddress.Loopback, 50000);
    private static readonly byte[] TxId = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
    private static readonly StunMessageCodec Codec = new();

    private static IceInboundCheckProcessor NewProcessor()
        => new(new IceInboundBindingResponder(Codec));

    private static byte[] BuildCheck(
        bool? peerControlling,
        ulong peerTieBreaker,
        bool useCandidate = false,
        string username = LocalUfrag + ":" + PeerUfrag,
        string signingPassword = LocalPassword)
    {
        var attributes = new List<StunAttribute>
        {
            new UsernameAttribute { Value = username },
            new PriorityAttribute { Value = 100u },
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
    public void Non_stun_datagram_is_not_an_ice_check()
    {
        var rtpLike = new byte[] { 0x80, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        var result = NewProcessor().Process(
            rtpLike, Sender, LocalUfrag, LocalPassword, IceRole.Controlled, ownTieBreaker: 1);

        Assert.False(result.IsIceCheck);
        Assert.Null(result.ResponseBytes);
    }

    [Fact]
    public void Accepted_check_produces_verifiable_success_response()
    {
        // We are controlled, peer controlling (no conflict).
        var raw = BuildCheck(peerControlling: true, peerTieBreaker: 5);

        var result = NewProcessor().Process(
            raw, Sender, LocalUfrag, LocalPassword, IceRole.Controlled, ownTieBreaker: 9);

        Assert.True(result.IsIceCheck);
        Assert.Equal(IceRole.Controlled, result.RoleAfter);
        Assert.False(result.NominatePair);

        var response = Codec.Decode(result.ResponseBytes!);
        Assert.NotNull(response);
        Assert.Equal(StunMessageClass.SuccessResponse, response!.MessageClass);
        Assert.Equal(Sender, response.Attributes.OfType<XorMappedAddressAttribute>().Single().EndPoint);
        Assert.True(Codec.VerifyIntegrity(result.ResponseBytes!, StunKeyDerivation.ShortTermKey(LocalPassword)));
    }

    [Fact]
    public void Bad_message_integrity_is_discarded()
    {
        var raw = BuildCheck(peerControlling: true, peerTieBreaker: 5, signingPassword: "wrongPassword");

        var result = NewProcessor().Process(
            raw, Sender, LocalUfrag, LocalPassword, IceRole.Controlled, ownTieBreaker: 9);

        Assert.True(result.IsIceCheck); // it *was* a STUN check
        Assert.Null(result.ResponseBytes); // but discarded, not answered
    }

    [Fact]
    public void Wrong_username_is_discarded()
    {
        var raw = BuildCheck(peerControlling: true, peerTieBreaker: 5, username: "notUs:peerUfrag");

        var result = NewProcessor().Process(
            raw, Sender, LocalUfrag, LocalPassword, IceRole.Controlled, ownTieBreaker: 9);

        Assert.True(result.IsIceCheck);
        Assert.Null(result.ResponseBytes);
    }

    [Fact]
    public void Role_conflict_lost_switches_to_controlled_and_answers_success()
    {
        // Both claim controlling; our tie-breaker is smaller → we switch to controlled and accept.
        var raw = BuildCheck(peerControlling: true, peerTieBreaker: 100);

        var result = NewProcessor().Process(
            raw, Sender, LocalUfrag, LocalPassword, IceRole.Controlling, ownTieBreaker: 50);

        Assert.Equal(IceRole.Controlled, result.RoleAfter);
        var response = Codec.Decode(result.ResponseBytes!);
        Assert.Equal(StunMessageClass.SuccessResponse, response!.MessageClass);
    }

    [Fact]
    public void Role_conflict_won_answers_with_verifiable_487()
    {
        // Both claim controlling; our tie-breaker is larger → we keep controlling and reject 487.
        var raw = BuildCheck(peerControlling: true, peerTieBreaker: 50);

        var result = NewProcessor().Process(
            raw, Sender, LocalUfrag, LocalPassword, IceRole.Controlling, ownTieBreaker: 100);

        Assert.Equal(IceRole.Controlling, result.RoleAfter);
        var response = Codec.Decode(result.ResponseBytes!);
        Assert.NotNull(response);
        Assert.Equal(StunMessageClass.ErrorResponse, response!.MessageClass);
        Assert.Equal(487, response.Attributes.OfType<ErrorCodeAttribute>().Single().Code);
        Assert.True(Codec.VerifyIntegrity(result.ResponseBytes!, StunKeyDerivation.ShortTermKey(LocalPassword)));
    }

    [Fact]
    public void Controlled_agent_nominates_on_use_candidate()
    {
        // We are controlled, peer controlling, USE-CANDIDATE present → nominate.
        var raw = BuildCheck(peerControlling: true, peerTieBreaker: 5, useCandidate: true);

        var result = NewProcessor().Process(
            raw, Sender, LocalUfrag, LocalPassword, IceRole.Controlled, ownTieBreaker: 1);

        Assert.True(result.NominatePair);
        var response = Codec.Decode(result.ResponseBytes!);
        Assert.Equal(StunMessageClass.SuccessResponse, response!.MessageClass);
    }
}
