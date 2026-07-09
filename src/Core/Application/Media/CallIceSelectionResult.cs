using System.Net;
using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Result of ICE candidate-pair selection for one call media leg.
/// </summary>
internal sealed class CallIceSelectionResult
{
    /// <summary>
    /// Final state reached by the ICE state machine.
    /// </summary>
    public required CallIceNegotiationState State { get; init; }

    /// <summary>
    /// True when ICE selected a candidate pair and endpoints should be overridden.
    /// </summary>
    public bool HasSelectedPair { get; init; }

    /// <summary>
    /// True when the selected pair was confirmed by a USE-CANDIDATE nomination check
    /// (RFC 8445 §8.1.1). False for an unnominated but reachable pair.
    /// </summary>
    public bool Nominated { get; init; }

    /// <summary>
    /// Selected local RTP endpoint when <see cref="HasSelectedPair"/> is true.
    /// </summary>
    public IPEndPoint? LocalEndPoint { get; init; }

    /// <summary>
    /// Selected remote RTP endpoint when <see cref="HasSelectedPair"/> is true.
    /// </summary>
    public IPEndPoint? RemoteEndPoint { get; init; }

    /// <summary>
    /// Selected local candidate when <see cref="HasSelectedPair"/> is true.
    /// </summary>
    public CallIceCandidate? LocalCandidate { get; init; }

    /// <summary>
    /// Selected remote candidate when <see cref="HasSelectedPair"/> is true.
    /// </summary>
    public CallIceCandidate? RemoteCandidate { get; init; }

    /// <summary>
    /// Stable reason code describing the selection outcome.
    /// </summary>
    public required string ReasonCode { get; init; }
}
