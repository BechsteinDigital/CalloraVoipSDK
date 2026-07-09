using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Client;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>
/// Builds an RFC 7675 consent-freshness check: an ICE connectivity-check Binding request
/// (RFC 8445 §7.2.2) addressed to the nominated peer and sent on the media socket. The USERNAME is
/// "{remote-ufrag}:{local-ufrag}" and MESSAGE-INTEGRITY is computed with the peer's password
/// (short-term credentials), matching an ordinary outbound connectivity check.
/// </summary>
internal static class IceConsentCheckBuilder
{
    /// <summary>
    /// Builds the encoded consent check and returns it together with its transaction ID (so the
    /// caller can register the transaction before sending and match the response).
    /// </summary>
    /// <param name="codec">The STUN wire codec.</param>
    /// <param name="localUfrag">This agent's local ICE username fragment.</param>
    /// <param name="remoteUfrag">The peer's ICE username fragment.</param>
    /// <param name="remotePassword">The peer's ICE password (used for MESSAGE-INTEGRITY).</param>
    /// <param name="priority">PRIORITY carried in the check (RFC 8445 §7.2.2).</param>
    /// <param name="controlling">Whether this agent holds the controlling role.</param>
    /// <param name="tieBreaker">This agent's 64-bit tie-breaker.</param>
    public static (byte[] Datagram, byte[] TransactionId) Build(
        IStunMessageCodec codec,
        string localUfrag,
        string remoteUfrag,
        string remotePassword,
        uint priority,
        bool controlling,
        ulong tieBreaker)
    {
        ArgumentNullException.ThrowIfNull(codec);

        var transactionId = StunMessage.CreateBindingRequest().TransactionId;

        var attributes = new List<StunAttribute>
        {
            new UsernameAttribute { Value = $"{remoteUfrag}:{localUfrag}" },
        };
        attributes.AddRange(StunIceCheckAttributes.Build(priority, controlling, tieBreaker, useCandidate: false));

        var request = new StunMessage
        {
            MessageClass = StunMessageClass.Request,
            MessageMethod = StunMessageMethod.Binding,
            TransactionId = transactionId,
            Attributes = attributes,
        };

        var datagram = codec.EncodeWithIntegrity(
            request, StunKeyDerivation.ShortTermKey(remotePassword), addFingerprint: true);
        return (datagram, transactionId);
    }
}
