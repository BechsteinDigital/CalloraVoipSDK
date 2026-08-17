using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Domain.Events;

/// <summary>
/// Event arguments for a change in inbound media flow on a call (#261, ADR-069): media went silent while the
/// peer is still demonstrably alive, or it resumed.
/// </summary>
public sealed class CallMediaFlowChangedEventArgs : EventArgs
{
    /// <summary>
    /// Creates event arguments for one media-flow transition.
    /// </summary>
    internal CallMediaFlowChangedEventArgs(bool inboundMediaFlowing, TimeSpan silenceDuration, ICall call)
    {
        InboundMediaFlowing = inboundMediaFlowing;
        SilenceDuration = silenceDuration;
        Call = call;
    }

    /// <summary>
    /// <see langword="false"/> when inbound media has gone silent, <see langword="true"/> when it resumed.
    /// Silence is not a failure by itself: silence suppression (RFC 3389), hold, and a bridge switch during a
    /// transfer all produce it while the far end is still reachable.
    /// </summary>
    public bool InboundMediaFlowing { get; }

    /// <summary>
    /// How long inbound media had been silent when this event was raised. On a resume this is the length of
    /// the silence that just ended.
    /// </summary>
    public TimeSpan SilenceDuration { get; }

    /// <summary>Call whose inbound media flow changed.</summary>
    public ICall Call { get; }
}
