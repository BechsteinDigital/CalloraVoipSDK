using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>
/// The ICE-relevant fields extracted from an inbound connectivity-check Binding request
/// (RFC 8445 §7.3). Carries the decoded <see cref="StunMessage"/> so the wire layer can build a
/// correlated response, plus the attributes the application decision logic
/// (<c>IceInboundCheckEvaluator</c>) needs to resolve role conflicts and nomination.
/// </summary>
/// <param name="Message">The decoded STUN Binding request.</param>
/// <param name="Username">The USERNAME attribute value ("{our-ufrag}:{peer-ufrag}"), or null.</param>
/// <param name="PeerControlling">
/// <see langword="true"/> when the request carried ICE-CONTROLLING, <see langword="false"/> for
/// ICE-CONTROLLED, <see langword="null"/> when neither role attribute was present.
/// </param>
/// <param name="PeerTieBreaker">
/// The 64-bit tie-breaker from the peer's role attribute (0 when no role attribute was present).
/// </param>
/// <param name="HasUseCandidate">Whether the request carried USE-CANDIDATE (RFC 8445 §7.3.1.5).</param>
/// <param name="Priority">The PRIORITY value from the request (0 when absent).</param>
internal readonly record struct IceInboundBindingRequest(
    StunMessage Message,
    string? Username,
    bool? PeerControlling,
    ulong PeerTieBreaker,
    bool HasUseCandidate,
    uint Priority);

/// <summary>
/// Wire layer for inbound ICE connectivity-check Binding requests (RFC 8445 §7.3): decodes the
/// request and its ICE attributes, verifies MESSAGE-INTEGRITY against the local ICE password
/// (short-term credentials, RFC 5389 §10.1), and builds the correlated Success response
/// (carrying XOR-MAPPED-ADDRESS) or the 487 Role Conflict error response (RFC 8445 §7.3.1.1).
/// <para>
/// This type is intentionally free of ICE decision logic (USERNAME targeting, role-conflict
/// resolution, nomination): those are decided by the application-layer <c>IceInboundCheckEvaluator</c>
/// from the fields on <see cref="IceInboundBindingRequest"/>. The transport layer wires the two
/// together, choosing which response this responder builds.
/// </para>
/// </summary>
internal sealed class IceInboundBindingResponder
{
    private readonly IStunMessageCodec _codec;

    /// <summary>
    /// Initialises the responder with the STUN wire codec used for decode/encode.
    /// </summary>
    public IceInboundBindingResponder(IStunMessageCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _codec = codec;
    }

    /// <summary>
    /// Attempts to decode a received datagram as an ICE connectivity-check Binding request and
    /// extract its ICE attributes. Returns <see langword="null"/> when the buffer is not a STUN
    /// message, or is not a Binding <em>request</em> (e.g. a Success response to one of our own
    /// checks, or a non-Binding method), so callers can fall through to their other receive paths.
    /// </summary>
    /// <param name="data">The raw received datagram.</param>
    public IceInboundBindingRequest? TryParse(ReadOnlySpan<byte> data)
    {
        var message = _codec.Decode(data);
        if (message is null)
            return null;

        if (message.MessageClass != StunMessageClass.Request
            || message.MessageMethod != StunMessageMethod.Binding)
        {
            return null;
        }

        string? username = null;
        bool? peerControlling = null;
        ulong peerTieBreaker = 0;
        bool hasUseCandidate = false;
        uint priority = 0;

        foreach (var attribute in message.Attributes)
        {
            switch (attribute)
            {
                case UsernameAttribute u:
                    username = u.Value;
                    break;
                case IceControllingAttribute c:
                    peerControlling = true;
                    peerTieBreaker = c.TieBreaker;
                    break;
                case IceControlledAttribute c:
                    peerControlling = false;
                    peerTieBreaker = c.TieBreaker;
                    break;
                case UseCandidateAttribute:
                    hasUseCandidate = true;
                    break;
                case PriorityAttribute p:
                    priority = p.Value;
                    break;
            }
        }

        return new IceInboundBindingRequest(
            message, username, peerControlling, peerTieBreaker, hasUseCandidate, priority);
    }

    /// <summary>
    /// Verifies the request's MESSAGE-INTEGRITY against the local ICE password. In ICE the sender
    /// authenticates a check with the password the receiver advertised, so the request and its
    /// response are both protected with this agent's own password (RFC 8445 §7.2.2, RFC 5389 §10.1).
    /// </summary>
    /// <param name="rawRequest">The complete raw request bytes as received.</param>
    /// <param name="localPassword">This agent's local ICE password (clear text).</param>
    public bool VerifyIntegrity(ReadOnlySpan<byte> rawRequest, string localPassword)
    {
        ArgumentNullException.ThrowIfNull(localPassword);
        var key = StunKeyDerivation.ShortTermKey(localPassword);
        return _codec.VerifyIntegrity(rawRequest, key);
    }

    /// <summary>
    /// Builds a Binding Success response for the given request: XOR-MAPPED-ADDRESS reflecting the
    /// sender's transport address, protected with MESSAGE-INTEGRITY (local password) and FINGERPRINT
    /// (RFC 8445 §7.3.1.2 / RFC 5389 §10.1.3).
    /// </summary>
    /// <param name="request">The decoded request to respond to (supplies the transaction ID).</param>
    /// <param name="sender">The transport address the request arrived from.</param>
    /// <param name="localPassword">This agent's local ICE password (clear text).</param>
    public byte[] BuildSuccessResponse(StunMessage request, IPEndPoint sender, string localPassword)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(localPassword);

        var response = StunMessage.CreateBindingResponse(
            request.TransactionId,
            [new XorMappedAddressAttribute { EndPoint = sender }]);

        var key = StunKeyDerivation.ShortTermKey(localPassword);
        return _codec.EncodeWithIntegrity(response, key, addFingerprint: true);
    }

    /// <summary>
    /// Builds a 487 Role Conflict error response for the given request, protected with
    /// MESSAGE-INTEGRITY (local password) and FINGERPRINT. Sent when the peer claims the same role
    /// and loses the tie-break so it must switch role and retry (RFC 8445 §7.3.1.1).
    /// </summary>
    /// <param name="request">The decoded request to respond to (supplies the transaction ID).</param>
    /// <param name="localPassword">This agent's local ICE password (clear text).</param>
    public byte[] BuildRoleConflictResponse(StunMessage request, string localPassword)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(localPassword);

        var response = new StunMessage
        {
            MessageClass = StunMessageClass.ErrorResponse,
            MessageMethod = StunMessageMethod.Binding,
            TransactionId = request.TransactionId,
            Attributes = [new ErrorCodeAttribute { Code = 487, Reason = "Role Conflict" }]
        };

        var key = StunKeyDerivation.ShortTermKey(localPassword);
        return _codec.EncodeWithIntegrity(response, key, addFingerprint: true);
    }
}
