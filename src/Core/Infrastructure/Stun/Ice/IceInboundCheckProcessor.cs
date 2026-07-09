using System.Net;
using CalloraVoipSdk.Core.Application.Media.Ice;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>
/// Outcome of processing one received datagram through the inbound ICE connectivity-check path.
/// </summary>
/// <param name="IsIceCheck">
/// <see langword="true"/> when the datagram was a STUN Binding request (an ICE check this path
/// consumes); <see langword="false"/> when it was not STUN, so the caller routes it to its RTP /
/// other receive path.
/// </param>
/// <param name="ResponseBytes">
/// The encoded response to send back to the sender (a Binding Success or a 487 Role Conflict), or
/// <see langword="null"/> when the check is silently discarded (authentication failed or the
/// USERNAME does not target this agent).
/// </param>
/// <param name="RoleAfter">This agent's role after any role-conflict resolution (RFC 8445 §7.3.1.1).</param>
/// <param name="NominatePair">
/// <see langword="true"/> when the peer's USE-CANDIDATE nominates the pair and this agent is the
/// controlled one (RFC 8445 §7.3.1.5); the transport layer acts on this.
/// </param>
/// <param name="Accepted">
/// <see langword="true"/> when the check authenticated and targeted this agent (a Success response
/// was produced). The transport layer triggers a connectivity check back to the sender to confirm
/// the pair in both directions (RFC 8445 §7.3.1.4).
/// </param>
internal readonly record struct IceInboundProcessingResult(
    bool IsIceCheck,
    byte[]? ResponseBytes,
    IceRole RoleAfter,
    bool NominatePair,
    bool Accepted);

/// <summary>
/// Ties the inbound ICE check pieces together (RFC 8445 §7.3): decodes and authenticates a received
/// datagram via <see cref="IceInboundBindingResponder"/>, applies the ICE semantics via the
/// application-layer <see cref="IceInboundCheckEvaluator"/> (USERNAME targeting, role-conflict
/// resolution, nomination), and produces the response to send plus the resulting role / nomination.
/// <para>
/// Stateless and transport-agnostic: all per-call ICE state (local ufrag/password, current role,
/// tie-breaker) is passed in so the transport layer that owns the media socket can drive this on
/// its receive path and apply <see cref="IceInboundProcessingResult.RoleAfter"/> to its own state.
/// Peer-reflexive candidate learning and triggered checks remain with the transport layer.
/// </para>
/// </summary>
internal sealed class IceInboundCheckProcessor
{
    private readonly IceInboundBindingResponder _responder;

    /// <summary>
    /// Initialises the processor with the wire responder used to decode requests and build responses.
    /// </summary>
    public IceInboundCheckProcessor(IceInboundBindingResponder responder)
    {
        ArgumentNullException.ThrowIfNull(responder);
        _responder = responder;
    }

    /// <summary>
    /// Processes one received datagram against the inbound ICE check path.
    /// </summary>
    /// <param name="data">The raw received datagram.</param>
    /// <param name="sender">The transport address the datagram arrived from.</param>
    /// <param name="localUfrag">This agent's local ICE username fragment.</param>
    /// <param name="localPassword">This agent's local ICE password (clear text).</param>
    /// <param name="currentRole">The role this agent currently holds.</param>
    /// <param name="ownTieBreaker">This agent's 64-bit tie-breaker.</param>
    public IceInboundProcessingResult Process(
        ReadOnlySpan<byte> data,
        IPEndPoint sender,
        string localUfrag,
        string localPassword,
        IceRole currentRole,
        ulong ownTieBreaker)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(localUfrag);
        ArgumentNullException.ThrowIfNull(localPassword);

        var parsed = _responder.TryParse(data);
        if (parsed is null)
            return new IceInboundProcessingResult(IsIceCheck: false, ResponseBytes: null, currentRole, NominatePair: false, Accepted: false);

        var request = parsed.Value;

        // RFC 8445 §7.3: authenticate the check with short-term credentials before acting on it.
        // A failed integrity check is discarded rather than answered, to avoid amplification.
        if (!_responder.VerifyIntegrity(data, localPassword))
            return new IceInboundProcessingResult(IsIceCheck: true, ResponseBytes: null, currentRole, NominatePair: false, Accepted: false);

        var decision = IceInboundCheckEvaluator.Evaluate(
            localUfrag,
            request.Username ?? string.Empty,
            currentRole,
            ownTieBreaker,
            request.PeerControlling,
            request.PeerTieBreaker,
            request.HasUseCandidate);

        if (decision.Reject487)
        {
            return new IceInboundProcessingResult(
                IsIceCheck: true,
                ResponseBytes: _responder.BuildRoleConflictResponse(request.Message, localPassword),
                decision.RoleAfter,
                NominatePair: false,
                Accepted: false);
        }

        // USERNAME does not target us (or was absent): discard silently (RFC 8445 §7.3).
        if (!decision.Accepted)
            return new IceInboundProcessingResult(IsIceCheck: true, ResponseBytes: null, decision.RoleAfter, NominatePair: false, Accepted: false);

        return new IceInboundProcessingResult(
            IsIceCheck: true,
            ResponseBytes: _responder.BuildSuccessResponse(request.Message, sender, localPassword),
            decision.RoleAfter,
            decision.NominatePair,
            Accepted: true);
    }
}
