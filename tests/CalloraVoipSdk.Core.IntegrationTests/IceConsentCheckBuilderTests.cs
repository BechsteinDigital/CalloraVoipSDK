using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Verifies the RFC 7675 consent-check builder produces a well-formed ICE connectivity-check
/// Binding request (RFC 8445 §7.2.2): USERNAME "{remote}:{local}", PRIORITY and role attributes,
/// MESSAGE-INTEGRITY under the peer password, and FINGERPRINT.
/// </summary>
public sealed class IceConsentCheckBuilderTests
{
    [Fact]
    public void Builds_a_verifiable_connectivity_check_for_the_peer()
    {
        var codec = new StunMessageCodec();

        var (datagram, transactionId) = IceConsentCheckBuilder.Build(
            codec, localUfrag: "localU", remoteUfrag: "peerU", remotePassword: "peerPwd",
            priority: 42u, controlling: true, tieBreaker: 7);

        var message = codec.Decode(datagram);
        Assert.NotNull(message);
        Assert.Equal(StunMessageClass.Request, message!.MessageClass);
        Assert.Equal(StunMessageMethod.Binding, message.MessageMethod);
        Assert.Equal(transactionId, message.TransactionId);
        Assert.Equal("peerU:localU", message.Attributes.OfType<UsernameAttribute>().Single().Value);
        Assert.Equal(42u, message.Attributes.OfType<PriorityAttribute>().Single().Value);
        Assert.Equal(7ul, message.Attributes.OfType<IceControllingAttribute>().Single().TieBreaker);
        Assert.True(codec.VerifyIntegrity(datagram, StunKeyDerivation.ShortTermKey("peerPwd")));
        Assert.True(codec.VerifyFingerprint(datagram));
    }

    [Fact]
    public void Controlled_agent_sends_ice_controlled()
    {
        var codec = new StunMessageCodec();

        var (datagram, _) = IceConsentCheckBuilder.Build(
            codec, "localU", "peerU", "peerPwd", 1u, controlling: false, tieBreaker: 3);

        var message = codec.Decode(datagram)!;
        Assert.Equal(3ul, message.Attributes.OfType<IceControlledAttribute>().Single().TieBreaker);
        Assert.Empty(message.Attributes.OfType<IceControllingAttribute>());
    }
}
